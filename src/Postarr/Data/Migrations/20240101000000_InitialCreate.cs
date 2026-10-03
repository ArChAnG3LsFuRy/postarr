using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Postarr.Data.Migrations;

/// <summary>
/// Initial migration — creates the full schema from scratch.
/// If an existing database already has these tables (created by EnsureCreated),
/// the DatabaseInitialiser handles that case by inserting a fake history record
/// so this migration is skipped safely.
/// </summary>
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.CreateTable("LibraryItems", t => new
        {
            Id                     = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true),
            PlexRatingKey          = t.Column<string>(nullable: false),
            PlexLibrarySectionId   = t.Column<string>(nullable: false),
            MediaType              = t.Column<int>(nullable: false),
            Title                  = t.Column<string>(nullable: false),
            Year                   = t.Column<int>(nullable: true),
            TmdbId                 = t.Column<string>(nullable: true),
            TvdbId                 = t.Column<string>(nullable: true),
            ImdbId                 = t.Column<string>(nullable: true),
            VideoResolution        = t.Column<string>(nullable: true),
            VideoDynamicRange      = t.Column<string>(nullable: true),
            AudioCodec             = t.Column<string>(nullable: true),
            Edition                = t.Column<string>(nullable: true),
            ContentRating          = t.Column<string>(nullable: true),
            Studio                 = t.Column<string>(nullable: true),
            Network                = t.Column<string>(nullable: true),
            CurrentPosterUrl       = t.Column<string>(nullable: true),
            CurrentPosterSource    = t.Column<string>(nullable: true),
            PosterAppliedToPlex    = t.Column<bool>(nullable: false, defaultValue: false),
            LastPosterAppliedUtc   = t.Column<DateTime>(nullable: true),
            CurrentBackgroundUrl   = t.Column<string>(nullable: true),
            CurrentBackgroundSource= t.Column<string>(nullable: true),
            BackgroundAppliedToPlex= t.Column<bool>(nullable: false, defaultValue: false),
            LastBackgroundAppliedUtc= t.Column<DateTime>(nullable: true),
            TextlessPreferred      = t.Column<bool>(nullable: false, defaultValue: false),
            StreamingService       = t.Column<string>(nullable: true),
            ShowStatus             = t.Column<string>(nullable: true),
            EpisodeCount           = t.Column<int>(nullable: true),
            ContentLanguage        = t.Column<string>(nullable: true),
            IsPopular              = t.Column<bool>(nullable: false, defaultValue: false),
            IsTrending             = t.Column<bool>(nullable: false, defaultValue: false),
            IsOscarWinner          = t.Column<bool>(nullable: false, defaultValue: false),
            IsOscarNominee         = t.Column<bool>(nullable: false, defaultValue: false),
            IsEmmyWinner           = t.Column<bool>(nullable: false, defaultValue: false),
            ImdbRating             = t.Column<double>(nullable: true),
            RottenTomatoesScore    = t.Column<int>(nullable: true),
            AudienceScore          = t.Column<int>(nullable: true),
            LastSeenAtUtc          = t.Column<DateTime>(nullable: false),
        },
        constraints: t => t.PrimaryKey("PK_LibraryItems", x => x.Id));
        m.CreateIndex("IX_LibraryItems_PlexRatingKey", "LibraryItems", "PlexRatingKey", unique: true);

        m.CreateTable("Seasons", t => new
        {
            Id                   = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true),
            LibraryItemId        = t.Column<int>(nullable: false),
            PlexRatingKey        = t.Column<string>(nullable: false),
            SeasonNumber         = t.Column<int>(nullable: false),
            Title                = t.Column<string>(nullable: false),
            EpisodeCount         = t.Column<int>(nullable: false, defaultValue: 0),
            IsNew                = t.Column<bool>(nullable: false, defaultValue: false),
            CurrentPosterUrl     = t.Column<string>(nullable: true),
            CurrentPosterSource  = t.Column<string>(nullable: true),
            LastPosterAppliedUtc = t.Column<DateTime>(nullable: true),
        },
        constraints: t =>
        {
            t.PrimaryKey("PK_Seasons", x => x.Id);
            t.ForeignKey("FK_Seasons_LibraryItems", x => x.LibraryItemId, "LibraryItems", "Id", onDelete: ReferentialAction.Cascade);
        });
        m.CreateIndex("IX_Seasons_PlexRatingKey", "Seasons", "PlexRatingKey", unique: true);

        m.CreateTable("Collections", t => new
        {
            Id                   = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true),
            PlexRatingKey        = t.Column<string>(nullable: false),
            PlexLibrarySectionId = t.Column<string>(nullable: false),
            MediaType            = t.Column<int>(nullable: false),
            Title                = t.Column<string>(nullable: false),
            ItemCount            = t.Column<int>(nullable: false, defaultValue: 0),
            CurrentPosterUrl     = t.Column<string>(nullable: true),
            CurrentPosterSource  = t.Column<string>(nullable: true),
            TmdbId               = t.Column<string>(nullable: true),
            LastSeenAtUtc        = t.Column<DateTime>(nullable: false),
        },
        constraints: t => t.PrimaryKey("PK_Collections", x => x.Id));
        m.CreateIndex("IX_Collections_PlexRatingKey", "Collections", "PlexRatingKey", unique: true);

        m.CreateTable("PendingChanges", t => new
        {
            Id                = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true),
            PlexRatingKey     = t.Column<string>(nullable: false),
            TargetTitle       = t.Column<string>(nullable: false),
            ChangeType        = t.Column<int>(nullable: false),
            ProposedImageUrl  = t.Column<string>(nullable: false),
            ProposedSource    = t.Column<int>(nullable: false),
            Reason            = t.Column<string>(nullable: false),
            CreatedAtUtc      = t.Column<DateTime>(nullable: false),
        },
        constraints: t => t.PrimaryKey("PK_PendingChanges", x => x.Id));

        m.CreateTable("ActivityLog", t => new
        {
            Id           = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true),
            TimestampUtc = t.Column<DateTime>(nullable: false),
            Message      = t.Column<string>(nullable: false),
            Level        = t.Column<string>(nullable: false, defaultValue: "Info"),
        },
        constraints: t => t.PrimaryKey("PK_ActivityLog", x => x.Id));

        m.CreateTable("Settings", t => new
        {
            Id          = t.Column<int>(nullable: false),
            JsonPayload = t.Column<string>(nullable: false, defaultValue: "{}"),
        },
        constraints: t => t.PrimaryKey("PK_Settings", x => x.Id));
    }

    protected override void Down(MigrationBuilder m)
    {
        m.DropTable("Seasons");
        m.DropTable("LibraryItems");
        m.DropTable("Collections");
        m.DropTable("PendingChanges");
        m.DropTable("ActivityLog");
        m.DropTable("Settings");
    }
}
