# Hiileike Home
This website links myself to my other homelab hosted sites.

## Planner

The Planner tile opens This Week. Use **Quick add** to capture ideas to the
Inbox, **Board** to schedule and move tasks, and **Projects** for roadmaps,
milestones, and subprojects. Open **Project Notes** from the Planner sidebar to
choose a project, or open it directly from a project or subproject. Tasks can
link to notes, which show backlinks to your own tasks.

Each project has its own notes and categories. Use **Manage categories** to
add or rename categories, and **Include subproject notes** on a parent to
include notes from all its descendants. A note shown on a parent still belongs
to the project where it was created.

On first use with an existing Lore catalog, Planner creates or reuses a
top-level **Demons Arcade** project and links it to that catalog. Existing
categories, bookmarks, revisions, links, drafts, and pictures remain available.
This migrated catalog retains the existing access for signed-in users.
Other project catalogs follow the Planner account's project access.

Pick a progress measure for each project. Task and milestone measures include
the work in subprojects. Average subproject progress weights each direct
subproject equally. Numeric goals track a current value and target, with an
optional weekly checkpoint for goals such as reading pages.

Project settings, task details, and milestone details each have a dashboard
visibility control. Hiding a project also hides its subprojects and their work.
Hidden items remain available in Planner and count toward project progress.
**Show hidden items** temporarily includes them in the dashboard.

### Storage

Planner uses the signed-in account's stable ID and a separate PostgreSQL schema.
With the existing Lore PostgreSQL configuration, it uses the same database
and the existing backup script includes both schemas. Planner creates its
tables on first use. If the app role cannot create schemas, create the schema
as a database administrator using `CREATE SCHEMA planner AUTHORIZATION hiileike;`
(substitute the configured app role). See the [backup instructions](Lore/Database/README.md#backups).

Set `ConnectionStrings__Planner` to use a separate database, such as when
running Lore from files. Back up that Planner database separately. With no
database configured, Planner saves locally in `App_Data/planner`. Set
`Planner__DataDirectory` to override that directory and back it up if using
file storage. Switching backends does not copy existing data between them.

-- Occasionally, when I'm not home some commits are made remotely with AI, if so I review them/ fix stuff when I'm back on my desk.
