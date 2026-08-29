using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using SkiaSharp;
using DWSIM.Interfaces;

namespace DWSIM.UI.Desktop.Avalonia;

/// <summary>
/// Gives the two flowsheet colour themes distinct artwork instead of leaving "Default" on the
/// schematic outline (a diamond with a "C", a circle with a zigzag):
///
///   Default     -> the flat icons the Objects palette shows, so the block you drop looks like the
///                  thumbnail you dragged.
///   Color Icons -> the photorealistic images the drawing assembly embeds, matching what the
///                  external biochemical units already do in that theme.
///
/// Both use the engine's existing image path: <c>DrawMode = 2</c> makes ShapeGraphic.DrawPhoto blit
/// whatever is in the object's photo fields. This class only decides which image goes in there.
/// The other themes (black-and-white PFD, the gradient modes) are left untouched.
///
/// The two fields are Friend to the drawing assembly, hence the reflection. The lookups are cached
/// and the class degrades to a no-op if a future engine version renames them, which just restores
/// the stock rendering.
/// </summary>
internal static class FlowsheetObjectIcons
{
    private const int DefaultTheme = 0;
    private const int ColorIconsTheme = 2;
    private const int PhotoDrawMode = 2;

    private static readonly Type GraphicObjectType =
        typeof(DWSIM.Drawing.SkiaSharp.GraphicObjects.GraphicObject);

    private static readonly FieldInfo? PhotoImageField =
        GraphicObjectType.GetField("PhotoImage", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? PhotoNameField =
        GraphicObjectType.GetField("EmbeddedResourcePhotoName", BindingFlags.NonPublic | BindingFlags.Instance);

    public static bool Available => PhotoImageField != null && PhotoNameField != null;

    /// <summary>
    /// Per-object artwork. Keyed weakly on the graphic object, so an object removed from the
    /// flowsheet takes its entry with it and a re-added one starts clean.
    /// </summary>
    private sealed class State
    {
        /// <summary>The embedded photo the object was built with, captured before it is overwritten.</summary>
        public string OriginalPhotoName = "";
        public SKImage? PaletteIcon;
        public SKImage? Photo;
        public bool PaletteTried;
        public bool PhotoTried;
    }

    private static readonly ConditionalWeakTable<IGraphicObject, State> _state = new();

    /// <summary>
    /// Streams are left alone: their graphic IS the connecting arrow, so an icon would hide where
    /// the material goes.
    /// </summary>
    private static bool IsExcluded(Interfaces.Enums.GraphicObjects.ObjectType type) =>
        type == Interfaces.Enums.GraphicObjects.ObjectType.MaterialStream ||
        type == Interfaces.Enums.GraphicObjects.ObjectType.EnergyStream;

    private static bool IsExternal(Interfaces.Enums.GraphicObjects.ObjectType type) =>
        type == Interfaces.Enums.GraphicObjects.ObjectType.External;

    private static readonly Dictionary<Type, FieldInfo?> _graphicArtworkFields = new();

    /// <summary>
    /// True when the graphic class itself holds the artwork it paints (a private SKImage cache
    /// named <c>Image</c>, <c>ImageOn</c>/<c>ImageOff</c> or <c>_photoImage</c>). Those graphics -
    /// the PID/MPC/Python controllers and the switches - paint more than a body in their Draw
    /// (leader lines, live readouts, state images), so the canvas override must leave them alone.
    ///
    /// The walk stops short of <see cref="GraphicObjectType"/>: the base class declares
    /// <c>Image</c> and <c>PhotoImage</c> for every graphic there is, so counting inherited fields
    /// matched all 57 concrete types and the override below could never fire - which is why the
    /// Logical blocks kept drawing their schematic spheres instead of the palette icon.
    /// </summary>
    private static bool GraphicHasOwnArtwork(Type type)
    {
        lock (_graphicArtworkFields)
        {
            if (_graphicArtworkFields.TryGetValue(type, out var cached)) return cached != null;

            FieldInfo? found = null;
            for (var t = type; t != null && t != GraphicObjectType && found == null; t = t.BaseType)
            {
                foreach (var name in new[] { "Image", "ImageOn", "ImageOff", "_photoImage" })
                {
                    var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f?.FieldType == typeof(SKImage)) { found = f; break; }
                }
            }

            _graphicArtworkFields[type] = found;
            return found != null;
        }
    }

    /// <summary>
    /// Instruments that render a live reading - the analog/digital/level gauges and the Input's
    /// editable value box. Their whole point is the number they show, so they keep their native
    /// draw; blitting the palette photograph over them would freeze the needle and hide the value.
    /// </summary>
    private static bool IsLiveReadout(Interfaces.Enums.GraphicObjects.ObjectType type) =>
        type == Interfaces.Enums.GraphicObjects.ObjectType.Input ||
        type == Interfaces.Enums.GraphicObjects.ObjectType.AnalogGauge ||
        type == Interfaces.Enums.GraphicObjects.ObjectType.DigitalGauge ||
        type == Interfaces.Enums.GraphicObjects.ObjectType.LevelGauge;

    /// <summary>
    /// The private SKImage cache each external unit operation passes ByRef to
    /// BioOpsDrawHelper.TryDrawPhotorealistic, looked up once per concrete type. The
    /// biochemical blocks name it "_photoImage"; the Clean Power classes (wind turbine,
    /// solar panel, ...) name theirs "Image". The field-type check keeps the generic
    /// "Image" name from matching anything that is not the cached artwork.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo?> _externalPhotoFields = new();

    private static FieldInfo? ExternalPhotoField(Type type)
    {
        lock (_externalPhotoFields)
        {
            if (_externalPhotoFields.TryGetValue(type, out var cached)) return cached;

            FieldInfo? found = null;
            for (var t = type; t != null && found == null; t = t.BaseType)
            {
                foreach (var name in new[] { "_photoImage", "Image" })
                {
                    var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f?.FieldType == typeof(SKImage)) { found = f; break; }
                }
            }

            _externalPhotoFields[type] = found;
            return found;
        }
    }

    /// <summary>
    /// Points every block at the artwork its theme calls for, and reports the theme value that has
    /// to be restored once the frame is drawn.
    ///
    /// DesignSurface.UpdateCanvas opens every frame with UpdateColorTheme(), which overwrites each
    /// object's DrawMode with FlowsheetOptions.FlowsheetColorTheme. Setting DrawMode here would
    /// therefore be undone before anything is painted - under "Default" the blocks would keep
    /// falling back to the outline. So the option itself is switched to the image mode for the
    /// duration of the (synchronous) draw and put back in <see cref="EndDraw"/>, leaving the value
    /// Simulation Settings reads untouched.
    ///
    /// Returns the theme to restore, or null when nothing was overridden.
    /// </summary>
    public static int? BeginDraw(IFlowsheet? flowsheet)
    {
        _activeTheme = null;

        if (!Available || flowsheet == null) return null;
        if (!UiPreferences.UsePaletteIconsOnCanvas) return null;

        int theme;
        try { theme = flowsheet.FlowsheetOptions.FlowsheetColorTheme; } catch { return null; }
        if (theme != DefaultTheme && theme != ColorIconsTheme) return null;

        // The surface override cannot read the theme back: the swap below puts the option on the
        // photo mode for the whole frame, so DrawMode is 2 under both themes by the time anything
        // paints. Remember which one we are really in.
        _activeTheme = theme;

        List<ISimulationObject> objects;
        try { objects = new List<ISimulationObject>(flowsheet.SimulationObjects.Values); }
        catch { return null; }

        foreach (var so in objects)
        {
            // Per object, so one type that misbehaves cannot abort the whole pass and leave the
            // rest of the flowsheet on the outline rendering.
            try
            {
                var gobj = so?.GraphicObject;
                if (gobj == null || IsExcluded(gobj.ObjectType)) continue;

                if (!_state.TryGetValue(gobj, out var state))
                {
                    state = new State();
                    try { state.OriginalPhotoName = PhotoNameField!.GetValue(gobj) as string ?? ""; }
                    catch { }
                    _state.Add(gobj, state);
                }

                if (IsExternal(gobj.ObjectType))
                    UseExternalArtwork(so!, state, wantPalette: theme == DefaultTheme);
                else if (theme == DefaultTheme)
                    UsePaletteIcon(so!, gobj, state);
                else
                    UseEmbeddedPhoto(gobj, state);
            }
            catch { }
        }

        if (theme == ColorIconsTheme) return null;   // already the mode UpdateColorTheme will set

        try
        {
            flowsheet.FlowsheetOptions.FlowsheetColorTheme = PhotoDrawMode;
            return theme;
        }
        catch { return null; }
    }

    /// <summary>Puts back the theme <see cref="BeginDraw"/> overrode. Safe to call with null.</summary>
    public static void EndDraw(IFlowsheet? flowsheet, int? original)
    {
        _activeTheme = null;
        if (flowsheet == null || original == null) return;
        try { flowsheet.FlowsheetOptions.FlowsheetColorTheme = original.Value; } catch { }
    }

    /// <summary>The theme of the frame being painted, as seen before <see cref="BeginDraw"/>
    /// swapped the option to the photo mode. Null outside a draw pass.</summary>
    private static int? _activeTheme;

    private static SKPaint? _iconPaint;

    /// <summary>
    /// Installs a surface-wide draw override so every block that belongs to a simulation object
    /// renders its palette icon under the Default theme. Custom-painting graphics (the Logical
    /// spheres, the controller panels, the Clean Power artwork) ignore the photo fields this class
    /// swaps for ShapeGraphics, and several of them only implement the schematic branch of their
    /// draw switch - without this they can never show the icon the palette thumbnail promised.
    /// Streams are left alone (their graphic is the connecting arrow), annotations have no owner,
    /// the Color Icons theme falls through to the native photorealistic rendering, and everything
    /// else keeps drawing itself. The native selection highlight is replicated because the engine
    /// skips it while an override is installed.
    /// </summary>
    public static void InstallSurfaceOverride(DWSIM.Drawing.SkiaSharp.GraphicsSurface surface)
    {
        surface.GlobalDrawOverride = (gobj, canvas) =>
        {
            if (!TryDrawCanvasIcon(gobj, canvas))
            {
                try { gobj.Draw(canvas); } catch { }
            }

            DrawTag(gobj, canvas);
            DrawSelectionGizmo(gobj, canvas);
        };
    }

    /// <summary>Object types whose graphic is its own label or has none - the annotations and the
    /// tables. Everything else gets its tag drawn under the block, as DesignSurface does natively.</summary>
    private static bool HasNoTag(Interfaces.Enums.GraphicObjects.ObjectType t) =>
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_MasterTable ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_SpreadsheetTable ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_Table ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_Animation ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_Chart ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_Rectangle ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_Image ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_Text ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_Button ||
        t == Interfaces.Enums.GraphicObjects.ObjectType.GO_FloatingTable;

    /// <summary>
    /// Draws the block's tag. DesignSurface only does this on the branch it takes when no override
    /// is installed (it sits inside the Else of the GlobalDrawOverride test), so with the override
    /// in place every label on the flowsheet would otherwise disappear.
    /// </summary>
    private static void DrawTag(IGraphicObject gobj, SKCanvas canvas)
    {
        if (HasNoTag(gobj.ObjectType)) return;
        if (gobj is not DWSIM.Drawing.SkiaSharp.GraphicObjects.ShapeGraphic shape) return;
        try { shape.DrawTag(canvas); } catch { }
    }

    private static bool TryDrawCanvasIcon(IGraphicObject gobj, SKCanvas canvas)
    {
        if (_state is null || !Available) return false;
        if (UiPreferences.UsePaletteIconsOnCanvas != true) return false;
        if (gobj.Owner == null) return false;
        if (IsExcluded(gobj.ObjectType)) return false;

        // Only the Default theme swaps in palette artwork; under Color Icons the blocks must keep
        // the photorealistic rendering they draw for themselves.
        if (_activeTheme != DefaultTheme) return false;

        var state = _state.TryGetValue(gobj, out var s) ? s : null;

        // Let the graphics that render their own artwork and overlays natively keep doing so:
        // the controllers draw the control-panel plus the dashed leader lines and the SP/PV/MV
        // readout, the switches draw their on/off state images and the gauges and inputs their
        // live reading - an icon blit would bury those, which is exactly why the controller's
        // dashed connections vanished when this override was installed.
        if (IsLiveReadout(gobj.ObjectType)) return false;
        if (GraphicHasOwnArtwork(gobj.GetType())) return false;

        // ShapeGraphics with an embedded photo would otherwise fall through to the native
        // DrawPhoto, which stretches the artwork across the whole block. Routing them through
        // here too is what makes every icon scale by the same rule - the columns' tall artwork
        // is letterboxed in the square box instead of being squashed into it. Connector
        // positions are set by the surface in its own pass, so nothing is lost by not calling
        // the native Draw.

        var icon = state?.PaletteIcon;
        if (icon == null)
        {
            // not swapped by BeginDraw (custom painter, or the pass never ran): pull the
            // palette artwork straight from the simulation object
            try
            {
                var bytes = gobj.Owner.GetIconBitmapBytes();
                icon = Decode(bytes);
                if (icon != null && state != null) { state.PaletteIcon = icon; state.PaletteTried = true; }
            }
            catch { return false; }
        }
        if (icon == null) return false;

        // No local rotation: DesignSurface has already applied the object's flip and rotation to
        // the canvas before invoking the override, so rotating again would double the angle.
        if (_iconPaint == null)
            _iconPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };

        var dest = FitRect(icon, gobj);

        canvas.DrawImage(icon, dest, _iconPaint);

        if (!gobj.Active || gobj.Status == Interfaces.Enums.GraphicObjects.Status.Inactive)
        {
            // grey the block out the way ShapeGraphic does for inactive objects
            using var p = new SKPaint { BlendMode = SKBlendMode.Color, ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Gray, SKBlendMode.SrcIn) };
            canvas.DrawImage(icon, dest, p);
        }

        return true;
    }

    /// <summary>
    /// The largest rectangle with the icon's aspect ratio that fits the block, centred on it. Every
    /// block now spawns in the same square box, so artwork that is not square (the columns, the PFR
    /// tube) is letterboxed instead of stretched to fill it.
    /// </summary>
    private static SKRect FitRect(SKImage icon, IGraphicObject gobj)
    {
        float w = gobj.Width, h = gobj.Height;
        if (icon.Width <= 0 || icon.Height <= 0 || w <= 0 || h <= 0)
            return new SKRect(gobj.X, gobj.Y, gobj.X + w, gobj.Y + h);

        var scale = Math.Min(w / icon.Width, h / icon.Height);
        var dw = icon.Width * scale;
        var dh = icon.Height * scale;
        var left = gobj.X + (w - dw) / 2f;
        var top = gobj.Y + (h - dh) / 2f;
        return new SKRect(left, top, left + dw, top + dh);
    }

    private static void DrawSelectionGizmo(IGraphicObject gobj, SKCanvas canvas)
    {
        if (!gobj.Selected) return;

        using var fill = new SKPaint { Color = SKColors.LightBlue.WithAlpha(75), IsAntialias = true, IsStroke = false };
        using var line = new SKPaint { Color = SKColors.LightBlue.WithAlpha(175), IsAntialias = true, IsStroke = true, StrokeWidth = 2 };

        // As above, the canvas already carries the object's transform.
        var rect = new SKRect(gobj.X - 10, gobj.Y - 10, gobj.X + gobj.Width + 10, gobj.Y + gobj.Height + 10);
        canvas.DrawRoundRect(rect, 4, 4, fill);
        canvas.DrawRoundRect(rect, 4, 4, line);
    }


    /// <summary>
    /// External unit operations (the biochemical blocks and friends) paint themselves in
    /// IExternalUnitOperation.Draw, so the graphic object's photo fields never come into play.
    /// What they do read, in the photo mode this class puts them into, is their own private SKImage
    /// cache - which BioOpsDrawHelper only fills when it is empty. Writing the palette icon into
    /// that cache is therefore enough to give them the same Default-theme look as every other block.
    /// </summary>
    private static void UseExternalArtwork(ISimulationObject so, State state, bool wantPalette)
    {
        var field = ExternalPhotoField(so.GetType());
        if (field == null) return;

        // Whatever the helper loaded for itself is the artwork to put back under Color Icons.
        var current = field.GetValue(so) as SKImage;
        if (current != null && !ReferenceEquals(current, state.PaletteIcon)) state.Photo = current;

        if (wantPalette)
        {
            EnsurePaletteIcon(so, state);
            if (state.PaletteIcon != null) field.SetValue(so, state.PaletteIcon);
            return;
        }

        // Null lets the helper reload its own image and cache it; the branch above captures it on
        // the next frame, so the swap back is a plain assignment from then on.
        if (!ReferenceEquals(current, state.Photo)) field.SetValue(so, state.Photo);
    }

    private static void EnsurePaletteIcon(ISimulationObject so, State state)
    {
        if (state.PaletteTried) return;
        state.PaletteTried = true;
        byte[]? bytes = null;
        try { bytes = so.GetIconBitmapBytes(); } catch { }
        state.PaletteIcon = Decode(bytes);
    }

    private static void UsePaletteIcon(ISimulationObject so, IGraphicObject gobj, State state)
    {
        EnsurePaletteIcon(so, state);

        // No palette artwork for this type: leave the outline the theme would have drawn.
        if (state.PaletteIcon == null) return;

        // A non-empty name is what tells DrawPhoto an image exists; the image is already set, so it
        // never resolves the name against the drawing assembly's resources.
        PhotoNameField!.SetValue(gobj, "palette-icon");
        PhotoImageField!.SetValue(gobj, state.PaletteIcon);
    }

    private static void UseEmbeddedPhoto(IGraphicObject gobj, State state)
    {
        // Nothing was ever swapped in, or the object has no photo of its own: stock behaviour.
        if (state.OriginalPhotoName.Length == 0) return;

        if (!state.PhotoTried)
        {
            state.PhotoTried = true;
            try
            {
                using var stream = GraphicObjectType.Assembly.GetManifestResourceStream(
                    "DWSIM.Drawing.SkiaSharp." + state.OriginalPhotoName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    state.Photo = Decode(ms.ToArray());
                }
            }
            catch { }
        }

        if (state.Photo == null) return;

        PhotoNameField!.SetValue(gobj, state.OriginalPhotoName);
        PhotoImageField!.SetValue(gobj, state.Photo);
    }

    private static SKImage? Decode(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            return bitmap == null ? null : SKImage.FromBitmap(bitmap);
        }
        catch { return null; }
    }
}
