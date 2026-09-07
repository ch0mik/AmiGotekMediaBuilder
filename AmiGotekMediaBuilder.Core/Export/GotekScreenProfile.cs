namespace AmiGotekMediaBuilder.Core.Export;

/// <summary>
/// A display profile supported by the Gotek Touchscreen interface project.
/// The dimensions are also the values used by the export gate for artwork.
/// Source: https://mesarim.github.io/Gotek-Touchscreen-interface/
/// </summary>
public sealed record GotekScreenProfile(
    string Id,
    string DisplayName,
    int Width,
    int Height,
    string Status)
{
    /// <summary>
    /// Profiles currently documented by the upstream project. Keep the
    /// recommended profile first so it is selected by default in both GUIs.
    /// </summary>
    public static IReadOnlyList<GotekScreenProfile> Supported { get; } =
    [
        new(
            "guition-jc3248w535c",
            "480 × 320 — Guition JC3248W535C 3.5\"",
            480,
            320,
            "recommended"),
        new(
            "waveshare-esp32-s3-touch-lcd-7",
            "800 × 480 — Waveshare ESP32-S3-Touch-LCD-7",
            800,
            480,
            "beta"),
        new(
            "waveshare-esp32-s3-touch-lcd-2-8",
            "320 × 240 — Waveshare ESP32-S3-Touch-LCD-2.8",
            320,
            240,
            "legacy")
    ];

    public static GotekScreenProfile Default => Supported[0];

    public override string ToString() => DisplayName;
}
