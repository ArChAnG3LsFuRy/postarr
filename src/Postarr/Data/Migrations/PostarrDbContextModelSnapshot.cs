using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Postarr.Data;

namespace Postarr.Data.Migrations;

[DbContext(typeof(PostarrDbContext))]
partial class PostarrDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder m)
    {
#pragma warning disable 612, 618
        m.HasAnnotation("ProductVersion", "8.0.10");

        m.Entity("Postarr.Models.LibraryItem", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("PlexRatingKey").IsRequired().HasColumnType("TEXT");
            b.Property<string>("PlexLibrarySectionId").IsRequired().HasColumnType("TEXT");
            b.Property<int>("MediaType").HasColumnType("INTEGER");
            b.Property<string>("Title").IsRequired().HasColumnType("TEXT");
            b.Property<int?>("Year").HasColumnType("INTEGER");
            b.Property<string?>("TmdbId").HasColumnType("TEXT");
            b.Property<string?>("TvdbId").HasColumnType("TEXT");
            b.Property<string?>("ImdbId").HasColumnType("TEXT");
            b.Property<string?>("VideoResolution").HasColumnType("TEXT");
            b.Property<string?>("VideoDynamicRange").HasColumnType("TEXT");
            b.Property<string?>("AudioCodec").HasColumnType("TEXT");
            b.Property<string?>("Edition").HasColumnType("TEXT");
            b.Property<string?>("ContentRating").HasColumnType("TEXT");
            b.Property<string?>("Studio").HasColumnType("TEXT");
            b.Property<string?>("Network").HasColumnType("TEXT");
            b.Property<string?>("CurrentPosterUrl").HasColumnType("TEXT");
            b.Property<string?>("CurrentPosterSource").HasColumnType("TEXT");
            b.Property<bool>("PosterAppliedToPlex").HasColumnType("INTEGER");
            b.Property<DateTime?>("LastPosterAppliedUtc").HasColumnType("TEXT");
            b.Property<string?>("CurrentBackgroundUrl").HasColumnType("TEXT");
            b.Property<string?>("CurrentBackgroundSource").HasColumnType("TEXT");
            b.Property<bool>("BackgroundAppliedToPlex").HasColumnType("INTEGER");
            b.Property<DateTime?>("LastBackgroundAppliedUtc").HasColumnType("TEXT");
            b.Property<bool>("TextlessPreferred").HasColumnType("INTEGER");
            b.Property<bool>("PosterDismissed").HasColumnType("INTEGER");
            b.Property<bool>("BackgroundDismissed").HasColumnType("INTEGER");
            b.Property<string?>("StreamingService").HasColumnType("TEXT");
            b.Property<string?>("ShowStatus").HasColumnType("TEXT");
            b.Property<int?>("EpisodeCount").HasColumnType("INTEGER");
            b.Property<string?>("ContentLanguage").HasColumnType("TEXT");
            b.Property<bool>("IsPopular").HasColumnType("INTEGER");
            b.Property<bool>("IsTrending").HasColumnType("INTEGER");
            b.Property<bool>("IsOscarWinner").HasColumnType("INTEGER");
            b.Property<bool>("IsOscarNominee").HasColumnType("INTEGER");
            b.Property<bool>("IsEmmyWinner").HasColumnType("INTEGER");
            b.Property<double?>("ImdbRating").HasColumnType("REAL");
            b.Property<int?>("RottenTomatoesScore").HasColumnType("INTEGER");
            b.Property<int?>("AudienceScore").HasColumnType("INTEGER");
            b.Property<DateTime>("LastSeenAtUtc").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("PlexRatingKey").IsUnique();
            b.ToTable("LibraryItems");
        });

        m.Entity("Postarr.Models.SeasonItem", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<int>("LibraryItemId").HasColumnType("INTEGER");
            b.Property<string>("PlexRatingKey").IsRequired().HasColumnType("TEXT");
            b.Property<int>("SeasonNumber").HasColumnType("INTEGER");
            b.Property<string>("Title").IsRequired().HasColumnType("TEXT");
            b.Property<int>("EpisodeCount").HasColumnType("INTEGER");
            b.Property<bool>("IsNew").HasColumnType("INTEGER");
            b.Property<string?>("CurrentPosterUrl").HasColumnType("TEXT");
            b.Property<string?>("CurrentPosterSource").HasColumnType("TEXT");
            b.Property<DateTime?>("LastPosterAppliedUtc").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("PlexRatingKey").IsUnique();
            b.HasIndex("LibraryItemId");
            b.ToTable("Seasons");
        });

        m.Entity("Postarr.Models.PlexCollection", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("PlexRatingKey").IsRequired().HasColumnType("TEXT");
            b.Property<string>("PlexLibrarySectionId").IsRequired().HasColumnType("TEXT");
            b.Property<int>("MediaType").HasColumnType("INTEGER");
            b.Property<string>("Title").IsRequired().HasColumnType("TEXT");
            b.Property<int>("ItemCount").HasColumnType("INTEGER");
            b.Property<string?>("CurrentPosterUrl").HasColumnType("TEXT");
            b.Property<string?>("CurrentPosterSource").HasColumnType("TEXT");
            b.Property<string?>("TmdbId").HasColumnType("TEXT");
            b.Property<DateTime>("LastSeenAtUtc").HasColumnType("TEXT");
            b.Property<bool>("PosterDismissed").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("PlexRatingKey").IsUnique();
            b.ToTable("Collections");
        });

        m.Entity("Postarr.Models.PendingChange", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("PlexRatingKey").IsRequired().HasColumnType("TEXT");
            b.Property<string>("TargetTitle").IsRequired().HasColumnType("TEXT");
            b.Property<int>("ChangeType").HasColumnType("INTEGER");
            b.Property<string>("ProposedImageUrl").IsRequired().HasColumnType("TEXT");
            b.Property<int>("ProposedSource").HasColumnType("INTEGER");
            b.Property<string>("Reason").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("PendingChanges");
        });

        m.Entity("Postarr.Models.ActivityLogEntry", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<DateTime>("TimestampUtc").HasColumnType("TEXT");
            b.Property<string>("Message").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Level").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("ActivityLog");
        });

        m.Entity("Postarr.Data.SettingsRecord", b =>
        {
            b.Property<int>("Id").HasColumnType("INTEGER");
            b.Property<string>("JsonPayload").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("Settings");
        });

        m.Entity("Postarr.Models.SeasonItem", b =>
        {
            b.HasOne("Postarr.Models.LibraryItem", null)
                .WithMany("Seasons")
                .HasForeignKey("LibraryItemId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });
#pragma warning restore 612, 618
    }
}
