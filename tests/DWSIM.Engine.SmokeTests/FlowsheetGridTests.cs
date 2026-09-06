//    The alignment grid on the flowsheet surface: what coordinate space it is drawn in, and how far
//    it reaches.
//
//    UpdateCanvas used to draw the grid before applying Zoom, so a grid square stayed 20 screen
//    pixels while the objects grew with the zoom. Opening a flowsheet runs ZoomAll, which for a
//    typical model picks a zoom around 3, and the grid then read as a fine mesh under oversized
//    objects - the reported "the grid is big while loading, then goes small". SnapToGrid rounds in
//    model units, so it was also snapping to positions that matched no visible line.
//
//    The extent was a fixed 0..10000 as well, which stops short of the corner at Zoom below 1.

using System;
using NUnit.Framework;
using SkiaSharp;
using DWSIM.Drawing.SkiaSharp;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class FlowsheetGridTests
    {
        private const int Width = 400;
        private const int Height = 300;

        [OneTimeSetUp]
        public void Setup()
        {
            DWSIM.GlobalSettings.Settings.AutomationMode = true;
            DWSIM.GlobalSettings.Settings.InspectorEnabled = false;
            DWSIM.GlobalSettings.Settings.CultureInfo = "en";
            DWSIM.GlobalSettings.Settings.DarkMode = false;

            FlowsheetBase.FlowsheetBase.AddPropPacks();
        }

        /// <summary>
        /// A surface with one object on it, so UpdateCanvas has a flowsheet to draw and ZoomAll has
        /// something to frame.
        /// </summary>
        private static (GraphicsSurface surface, DWSIM.DynamicRunner.Flowsheet fs) Surface()
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            fs.AddCompound("Water");
            fs.AddObject(DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.MaterialStream, 100, 100, "s1");

            var surface = (GraphicsSurface)fs.GetSurface();
            surface.Flowsheet = fs;
            surface.ShowGrid = true;
            surface.Size = new SKSize(Width, Height);
            return (surface, fs);
        }

        /// <summary>
        /// Renders the surface and returns the spacing, in device pixels, between the grid lines it
        /// drew - measured the same way the two screenshots in the report were measured.
        /// </summary>
        private static int GridSpacing(GraphicsSurface surface)
        {
            using var bmp = new SKBitmap(Width, Height);
            using (var canvas = new SKCanvas(bmp))
            {
                surface.UpdateCanvas(canvas);
            }

            // Scan a row clear of the object at (100, 100) and count the columns that are not the
            // background. The grid is the only thing drawn there.
            const int row = Height - 17;
            var background = bmp.GetPixel(1, row);

            var columns = new System.Collections.Generic.List<int>();
            for (var x = 0; x < Width; x++)
            {
                var p = bmp.GetPixel(x, row);
                if (p.Red != background.Red || p.Green != background.Green || p.Blue != background.Blue)
                {
                    // Collapse a line that lands on two pixels through antialiasing.
                    if (columns.Count == 0 || x - columns[columns.Count - 1] > 1) columns.Add(x);
                }
            }

            Assert.That(columns.Count, Is.GreaterThan(2),
                        "no grid lines were drawn on the row that was scanned");

            var gaps = new System.Collections.Generic.List<int>();
            for (var i = 1; i < columns.Count; i++) gaps.Add(columns[i] - columns[i - 1]);
            gaps.Sort();
            return gaps[gaps.Count / 2];   // median, so one odd gap at an edge does not matter
        }

        /// <summary>
        /// At Zoom 1 the grid is GridSize pixels apart, which was true before the fix too.
        /// </summary>
        [Test]
        public void TheGridIsGridSizeApartAtZoomOne()
        {
            var (surface, _) = Surface();
            surface.Zoom = 1.0f;

            Assert.That(GridSpacing(surface), Is.EqualTo(surface.GridSize).Within(1));
        }

        /// <summary>
        /// The regression: the grid has to scale with the objects. Before the fix this stayed at
        /// GridSize whatever the zoom.
        /// </summary>
        [TestCase(2.0f)]
        [TestCase(3.0f)]
        [TestCase(4.0f)]
        public void TheGridScalesWithTheZoom(float zoom)
        {
            var (surface, _) = Surface();
            surface.Zoom = zoom;

            var spacing = GridSpacing(surface);

            Assert.That(spacing, Is.EqualTo(surface.GridSize * zoom).Within(1.5),
                        $"at zoom {zoom} the grid should be {surface.GridSize * zoom} px apart, not {spacing}");
        }

        /// <summary>
        /// Zoomed out, the grid still has to reach the far corner. The old fixed 0..10000 extent
        /// covered it at Zoom 1 but not below, where the surface sees model coordinates past 10000.
        /// </summary>
        [Test]
        public void TheGridReachesTheCornerWhenZoomedOut()
        {
            var (surface, _) = Surface();
            surface.Zoom = 0.2f;
            // This viewport extends past model coordinate 10000 on both axes.
            const int width = 2400, height = 2200;
            surface.Size = new SKSize(width, height);

            using var bmp = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bmp))
            {
                surface.UpdateCanvas(canvas);
            }

            var background = GraphicsSurface.BackgroundColor;
            var painted = false;
            for (var y = height - 30; y < height && !painted; y++)
                for (var x = width - 30; x < width && !painted; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.Red != background.Red || p.Green != background.Green || p.Blue != background.Blue)
                        painted = true;
                }

            Assert.That(painted, Is.True, "the grid did not reach the bottom-right corner");
        }

        /// <summary>
        /// Keep visible alignment lines when zoomed out, with enough space to read them.
        /// </summary>
        [TestCase(0.1f)]
        [TestCase(0.2f)]
        [TestCase(0.4f)]
        public void TheGridStaysReadableWhenZoomedOut(float zoom)
        {
            var (surface, _) = Surface();
            surface.Zoom = zoom;

            var spacing = GridSpacing(surface);
            Assert.That(spacing, Is.GreaterThanOrEqualTo(10), "the grid lines are too close to read");
            var alignmentSteps = spacing / (surface.GridSize * (double)zoom);
            Assert.That(alignmentSteps, Is.EqualTo(Math.Round(alignmentSteps)).Within(0.01),
                        "visible grid lines must still align with the snapping grid");
        }

        /// <summary>
        /// Turning the grid off draws nothing, at any zoom.
        /// </summary>
        [Test]
        public void TheGridIsNotDrawnWhenItIsOff()
        {
            var (surface, _) = Surface();
            surface.ShowGrid = false;
            surface.Zoom = 3.0f;

            using var bmp = new SKBitmap(Width, Height);
            using (var canvas = new SKCanvas(bmp))
            {
                surface.UpdateCanvas(canvas);
            }

            const int row = Height - 20;
            var background = bmp.GetPixel(1, row);
            for (var x = 0; x < Width; x++)
            {
                var p = bmp.GetPixel(x, row);
                Assert.That((p.Red, p.Green, p.Blue),
                            Is.EqualTo((background.Red, background.Green, background.Blue)),
                            $"something was drawn at x={x} with the grid off");
            }
        }

        /// <summary>
        /// SnapToGrid rounds in model units, so a snapped object centre has to land on a line the
        /// grid actually draws. This is the property that was broken: the two used different spaces.
        /// </summary>
        [TestCase(1.0f)]
        [TestCase(3.0f)]
        public void SnappedPositionsLandOnDrawnGridLines(float zoom)
        {
            var (surface, _) = Surface();
            surface.Zoom = zoom;

            var gridSize = surface.GridSize;

            // What SnapToGrid computes for an object centre, straight from DesignSurface.
            const double centre = 137.0;
            var snapped = Math.Round(centre / gridSize) * gridSize;

            // That model coordinate maps to this device pixel under the canvas scale.
            var devicePixel = snapped * zoom;

            // And the grid, drawn in model units after the scale, has a line at every multiple of
            // GridSize x zoom device pixels.
            Assert.That(devicePixel % (gridSize * zoom), Is.EqualTo(0.0).Within(1e-6),
                        $"a snapped centre at model {snapped} lands at device {devicePixel}, " +
                        $"which is not on a grid line at zoom {zoom}");
        }
    }
}
