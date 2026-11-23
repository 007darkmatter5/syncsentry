namespace SyncSentry.Customizations;

public static class CustomizationsSample
{
    public const string Json = """
        {
          "schema_version": 1,
          "generated_at": "2026-11-22T18:40:00Z",
          "last_verified": "2026-11-22T18:12:00Z",
          "servers": [
            "UnraidHive",
            { "name": "nas-unraid", "role": "backup", "notes": "second server" }
          ],
          "counts": { "changes": 3, "pending": 1, "observations": 2, "must_check": 1 },
          "changes": [
            {
              "id": "H-04",
              "server": "UnraidHive",
              "category": "copy",
              "title": "Movies copy excludes Syncthing temp files",
              "summary": "The rclone copy from UNASPRO Media to the array skips .stfolder, partials, and Syncthing temp names.",
              "before": "rclone copy copied every file the share contained, including Syncthing's own bookkeeping.",
              "after": "The copy script passes exclude filters for .stfolder/**, *.partial, .rclone*, and ~syncthing~*.tmp.",
              "why": "Those files are not media. Copying them made History noisy and wasted the link.",
              "date": "2026-11-18",
              "made_by": "owner",
              "made_by_confidence": "high",
              "files": [
                "/boot/config/plugins/user.scripts/scripts/syncsentry-jobs/media-copy.sh"
              ],
              "backups": [
                "/mnt/cache/appdata/mediamanny/customizations/backups/media-copy.sh.2026-11-18"
              ],
              "undo": [
                "Open the media-copy script on the Unraid flash.",
                "Remove the exclude lines added on 2026-11-18.",
                "The backup beside this note is the previous script."
              ],
              "active": "yes",
              "active_detail": "The running script still has the exclude lines.",
              "verified_at": "2026-11-22T18:12:00Z",
              "warnings": [],
              "tags": ["must-check-before-work"],
              "ticket": "ignored-extra-field"
            },
            {
              "id": "H-11",
              "server": "UnraidHive",
              "category": "network",
              "title": "Tailscale accept-routes left on for the UNAS subnet",
              "summary": "UnraidHive accepts subnet routes so 192.168.1.20 (the UNAS) stays reachable.",
              "before": "Accept routes was off after a Tailscale upgrade.",
              "after": "RouteAll is true. The host helper writes that into tailscale.json every minute.",
              "why": "Without the route, the CIFS mount to UNASPRO Media goes stale.",
              "date": "2026-11-12",
              "made_by": "host script",
              "made_by_confidence": "medium",
              "files": ["/mnt/user/appdata/syncsentry/host/tailscale.json"],
              "backups": [],
              "undo": "In Tailscale, turn off accept routes. The UNAS share will drop.",
              "active": "yes",
              "active_detail": "",
              "verified_at": "2026-11-22T18:00:00Z",
              "warnings": ["Do not turn accept routes off while a copy is running."],
              "tags": ["review"]
            },
            {
              "id": "N-02",
              "server": "nas-unraid",
              "category": "share",
              "title": "Old backup share left exported",
              "summary": "nas-unraid still exports a share named old-backup that nothing copies anymore.",
              "before": "Share exported read/write on the LAN.",
              "after": "Not changed yet.",
              "why": "Noted during the November check. It may be unused.",
              "date": "2026-11-20",
              "made_by": "unknown",
              "made_by_confidence": "low",
              "files": [],
              "backups": [],
              "undo": "",
              "active": "unknown",
              "active_detail": "The export was not re-checked after the note was written.",
              "verified_at": "",
              "warnings": ["Confirm nothing still writes here before removing the export."],
              "tags": ["unverified", "cleanup"]
            }
          ],
          "pending": [
            {
              "id": "P-03",
              "server": "nas-unraid",
              "title": "Confirm old-backup is unused",
              "detail": "Ask before deleting the old-backup share. The note N-02 is only a guess.",
              "source": "November check"
            }
          ],
          "observations": [
            {
              "id": "O-01",
              "server": "UnraidHive",
              "severity": "warning",
              "title": "UNASPRO Media was stale once this week",
              "detail": "The CIFS mount stopped answering for a few minutes on Nov 21, then recovered. No copy was running."
            },
            {
              "id": "O-02",
              "server": "nas-unraid",
              "severity": "info",
              "title": "Cache drive has room",
              "detail": "More than 200 GB free on the cache pool when this file was generated."
            }
          ]
        }
        """;
}
