# Postarr Database Migrations

> ⚠️ **Read this before adding a column.** The migration files in this folder are **not** actually
> applied. EF only discovers a migration that carries `[Migration("<id>")]` and
> `[DbContext(typeof(PostarrDbContext))]` attributes — ours have neither, so `MigrateAsync()` finds
> nothing and is a no-op. The schema really comes from `EnsureCreatedAsync()` building the current
> model, which covers a **new** database but never alters an **existing** one.
>
> **To add a column today:** add the property to the model, then add one
> `EnsureColumnAsync(conn, "<Table>", "<Column>", "<TYPE>", "<default>")` line in
> `DatabaseInitialiser`. It checks `pragma_table_info` first, so it's idempotent — new databases
> skip it, existing ones get the column. (See `PosterDismissed` / `BackgroundDismissed` for an
> example.) Adding a migration file alone will silently do nothing.
>
> The rest of this document describes the intended EF migration flow, kept for reference if the
> attributes are ever added and the system is switched over properly.

## How it works

Postarr uses EF Core migrations so **your database is preserved across updates**. 
No more losing API keys or rescanning your library when you update.

### On startup

`DatabaseInitialiser.InitialiseAsync()` runs automatically and:
1. **Fresh install** â€” creates schema from all migrations in order
2. **Existing DB (old EnsureCreated style)** â€” registers the initial migration as already done, then applies any new ones on top. All your data is kept.
3. **Already migrated DB** â€” applies only pending new migrations. All your data is kept.

---

## Adding a new migration (when you add a model property)

### Step 1 â€” Create the migration file

Name it: `YYYYMMDDHHMMSS_DescriptionOfChange.cs`

Example for adding a `MyNewColumn` to `LibraryItems`:

```csharp
// Data/Migrations/20240615120000_AddMyNewColumn.cs
using Microsoft.EntityFrameworkCore.Migrations;

namespace Postarr.Data.Migrations;

public partial class AddMyNewColumn : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        // SQLite ALTER TABLE ADD COLUMN â€” always include a DEFAULT so existing rows are valid
        m.AddColumn<string>(
            name: "MyNewColumn",
            table: "LibraryItems",
            nullable: true,
            defaultValue: null);
    }

    protected override void Down(MigrationBuilder m)
    {
        // SQLite doesn't support DROP COLUMN in older versions â€” leave empty or recreate table
    }
}
```

### Step 2 â€” Update the snapshot

In `PostarrDbContextModelSnapshot.cs`, add the new property to the relevant entity's `BuildModel` block:

```csharp
b.Property<string?>("MyNewColumn").HasColumnType("TEXT");
```

### Step 3 â€” Done

On next startup, `MigrateAsync()` sees the new migration hasn't been applied and runs it automatically. Existing data is untouched.

---

## Rules for safe SQLite migrations

- **Always provide a DEFAULT** when adding a non-nullable column to an existing table
- **Don't rename columns** â€” add a new one and copy data in a subsequent migration
- **SQLite doesn't support** dropping columns, changing column types, or most ALTER TABLE operations beyond ADD COLUMN â€” recreate the table if you need those
- **Test with a copy** of your real DB before rolling out to production
