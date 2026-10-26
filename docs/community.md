# Project community and guide

AI Nexus uses [GitHub Discussions](https://github.com/HopeCrushersInc/AINexus/discussions) as its public project forum. This is the place to check announcements, ask questions and discuss the roadmap. The repository and its discussions are public: share only information intended for public viewing.

## Where to communicate

| Place | Purpose |
| --- | --- |
| [Announcements](https://github.com/HopeCrushersInc/AINexus/discussions/categories/announcements) | Published project updates and important notices |
| [Q&A](https://github.com/HopeCrushersInc/AINexus/discussions/categories/q-a) | Setup questions and help using the portal |
| [Ideas](https://github.com/HopeCrushersInc/AINexus/discussions/categories/ideas) | Feature suggestions, proposed workflows and roadmap feedback |
| [General](https://github.com/HopeCrushersInc/AINexus/discussions/categories/general) | Open project conversation |
| [Show and tell](https://github.com/HopeCrushersInc/AINexus/discussions/categories/show-and-tell) | Share working setups and results |
| [Polls](https://github.com/HopeCrushersInc/AINexus/discussions/categories/polls) | Gather community preferences; polls are not implementation commitments |
| [Issues](https://github.com/HopeCrushersInc/AINexus/issues) | Reproducible bugs and agreed work items |
| [Pull requests](https://github.com/HopeCrushersInc/AINexus/pulls) | Review proposed source and documentation changes |
| [Changelog](../CHANGELOG.md) | Read what has actually been published, including validation and limitations |

To receive updates in GitHub notifications, optionally use the repository's **Watch** settings and choose the discussion, issue and pull-request activity you want to follow. Following a discussion also helps keep a particular conversation easy to revisit.

For a bug report, include the portal version or commit, operating system and architecture, steps to reproduce, expected result and actual result. Remove credentials, private prompts, personal paths and other sensitive information from logs before posting. A compatibility estimate is not a completed runtime verification.

## Guide index

These versioned repository pages form the project's current wiki-style guide:

- [Project introduction and build instructions](../README.md)
- [Feature overview](feature-overview.md)
- [Development roadmap and feature projection](development-roadmap.md)
- [Published changelog](../CHANGELOG.md)
- [Engineering state and validation evidence](../DEVELOPMENT.md)

The separate [GitHub Wiki](https://github.com/HopeCrushersInc/AINexus/wiki) feature is enabled, but its first page has not been initialized. The repository guide above is the available documentation; it does not depend on a separate wiki. Keeping the guide in the repository subjects edits to the normal review history and branch protections.

## Proposed private Discord space

A private Discord server is planned as an optional companion for private team conversation. **It has not been created or connected, and no invitation is available yet.** Public decisions, accepted work and published changes should still be recorded in GitHub so that project information is easy to find later.

Proposed channels:

- `welcome-and-rules`: purpose, access expectations and links to GitHub.
- `announcements`: owner-maintained notices and published-update links.
- `project-chat`: everyday team conversation.
- `development`: implementation coordination and review links.
- `models-and-runtimes`: compatibility observations and maintenance discussion.
- `testing-and-feedback`: test results and bug-report preparation.
- `voice-workroom`: optional live collaboration.

Proposed access policy, to be verified during setup:

- Invite-only membership, with invitations controlled by the owner.
- An Owner role for administration, a narrowly scoped Moderator role, and a Member role for ordinary participation.
- No administrator permission or private-channel access for `@everyone`; members receive explicit access through their role.
- Restrict broad `@everyone`/`@here` mentions and role mentions to authorized announcement roles.
- No bots, webhooks or automated source/log forwarding initially. Add integrations only after their access and data flow are reviewed.
- Use ordinary free server features; no paid subscription or boost is required by this plan.

A private server still requires deliberate handling of secrets and project data. Do not post access tokens, account recovery information or private user data in chat. Verify the server's roles and channel visibility before inviting collaborators.
