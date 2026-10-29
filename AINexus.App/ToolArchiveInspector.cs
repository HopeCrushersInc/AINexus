using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AINexus.App;

public sealed record ToolArchiveInspectionRequest(string ArchivePath, long ExpectedBytes, string ExpectedSha256);

/// <summary>Runs archive metadata parsing in an owned, resource-limited copy of the portal. Never extracts or executes payloads.</summary>
public static class ToolArchiveInspector
{
    internal const int MaximumReportBytes = 32 * 1024 * 1024;
    internal const long WorkerMemoryBytes = 768L * 1024 * 1024;
    private const string WorkerArgument = "--inspect-sevenzip-worker";

    public static async Task<ToolArchiveInspection> InspectAsync(ToolArchiveInspectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        OfflineModelVerifier.RequireLocalDrivePath(request.ArchivePath);
        var archivePath = Path.GetFullPath(request.ArchivePath);
        OfflineModelVerifier.CheckNoReparsePath(archivePath);
        if (request.ExpectedBytes is <= 0 or > 32L * 1024 * 1024 * 1024
            || request.ExpectedSha256 is null || request.ExpectedSha256.Length != 64 || !request.ExpectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Inspection requires an exact positive archive size and SHA-256.");
        request = request with { ArchivePath = archivePath };
        var executable = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        OfflineModelVerifier.CheckNoReparsePath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("The portal's own archive inspector host is missing.", executable);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        info.ArgumentList.Add(WorkerArgument);
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["DOTNET_EnableDiagnostics"] = "0";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        using var process = new Process { StartInfo = info };
        using var job = InspectionJob.Create();
        if (!process.Start()) throw new InvalidOperationException("Could not start the portal's archive inspector.");
        try
        {
            // The worker reads no archive until this job is assigned and input arrives.
            job.Assign(process);
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumReportBytes, token);
            var errors = ReadBoundedAsync(process.StandardError.BaseStream, 8192, token);
            _ = output.ContinueWith(_ => CancelIfActive(deadline), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = errors.ContinueWith(_ => CancelIfActive(deadline), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var input = JsonSerializer.SerializeToUtf8Bytes(request);
            if (input.Length > 16384) throw new InvalidDataException("The archive inspection request is too large.");
            await process.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false);
            process.StandardInput.Close();
            // Observe output limits while the worker runs, rather than after waiting for exit.
            await Task.WhenAll(process.WaitForExitAsync(token), output, errors).WaitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException("Archive inspection failed or exceeded its resource limit. "
                    + System.Text.Encoding.UTF8.GetString(await errors.ConfigureAwait(false)));
            var result = JsonSerializer.Deserialize<ToolArchiveInspection>(await output.ConfigureAwait(false), new JsonSerializerOptions { MaxDepth = 16 })
                ?? throw new InvalidDataException("The inspector returned no report.");
            if (result.ArchivePath != archivePath || result.ArchiveBytes != request.ExpectedBytes
                || !result.Sha256.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
                || result.Entries is null || result.Entries.Count > 100000 || result.DeclaredExpandedBytes < 0)
                throw new InvalidDataException("The inspector report does not match the selected archive.");
            return result;
        }
        finally
        {
            // The Job handle owns only this new worker. Closing it terminates any still-running worker.
            job.Dispose();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsWorker(string[] args) => args.Length == 1 && args[0] == WorkerArgument;
    private static void CancelIfActive(CancellationTokenSource source)
    {
        try { source.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal static async Task<int> RunWorkerAsync()
    {
        try
        {
            // Input itself is bounded; no archive/header is parsed before verifying the OS memory/process limits.
            var input = await ReadBoundedAsync(Console.OpenStandardInput(), 16384, CancellationToken.None).ConfigureAwait(false);
            InspectionJob.RequireCurrentLimits();
            var request = JsonSerializer.Deserialize<ToolArchiveInspectionRequest>(input, new JsonSerializerOptions { MaxDepth = 4 })
                ?? throw new InvalidDataException("Missing archive identity.");
            var result = await SevenZipInspection.InspectAsync(request.ArchivePath, request.ExpectedBytes, request.ExpectedSha256, CancellationToken.None).ConfigureAwait(false);
            using var output = new LimitedOutputStream(Console.OpenStandardOutput(), MaximumReportBytes);
            JsonSerializer.Serialize(output, result);
            await output.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (OutOfMemoryException)
        {
            return 3;
        }
        catch (Exception error)
        {
            var message = error.Message;
            await Console.Error.WriteAsync(message.Length > 2048 ? message[..2048] : message).ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("The archive inspector exceeded its request/report bound.");
            output.Write(buffer, 0, count);
        }
    }

    private sealed class LimitedOutputStream(Stream output, int maximum) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_written + buffer.Length > maximum) throw new InvalidDataException("The archive report exceeded its output bound.");
            output.Write(buffer); _written += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class InspectionJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private const uint Flags = 0x100 | 0x2000 | 0x8; // Process memory, kill-on-close, one active process.
        private InspectionJob() : base(true) { }
        internal static InspectionJob Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits { LimitFlags = Flags, ActiveProcessLimit = 1 },
                ProcessMemoryLimit = (UIntPtr)WorkerMemoryBytes
            };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            { var error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error); }
            return job;
        }
        internal void Assign(Process process)
        {
            if (!AssignProcessToJobObject(this, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not apply the archive worker limits.");
        }
        internal static void RequireCurrentLimits()
        {
            if (!QueryInformationJobObject(IntPtr.Zero, 9, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero)
                || (limits.Basic.LimitFlags & Flags) != Flags || limits.Basic.ActiveProcessLimit != 1
                || limits.ProcessMemoryLimit.ToUInt64() is 0 or > (ulong)WorkerMemoryBytes)
                throw new InvalidOperationException("Archive parsing requires the portal's resource-limited worker.");
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long ProcessUserTime, JobUserTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern InspectionJob CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(InspectionJob job, int type, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(InspectionJob job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int type, out ExtendedLimits limits, uint length, IntPtr returnedLength);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
