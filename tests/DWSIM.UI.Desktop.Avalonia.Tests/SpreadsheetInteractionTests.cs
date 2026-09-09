using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;
using unvell.ReoGrid;
using unvell.ReoGrid.DataFormat;

namespace DWSIM.UI.Desktop.Avalonia.Tests;

public static class SpreadsheetTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[TestFixture, NonParallelizable]
public sealed class SpreadsheetInteractionTests
{
    private HeadlessUnitTestSession _session = null!;

    [OneTimeSetUp]
    public void StartSession() => _session = HeadlessUnitTestSession.StartNew(typeof(SpreadsheetTestApp));

    [OneTimeTearDown]
    public void StopSession() => _session.Dispose();

    private void InSpreadsheet(Action<SpreadsheetWindow> test, double scale = 1) =>
        _session.Dispatch(() =>
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                using var view = new SpreadsheetWindow(scale);
                test(view);
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestCase("0", "1", "0")]
    [TestCase("2", "1.23", "0.14")]
    [TestCase("8", "1.23456789", "0.14285714")]
    public void ColumnDecimalsRefreshNumbersAndFormulasWithoutChangingTheirValues(
        string decimals, string number, string formula) => InSpreadsheet(view =>
    {
        view.Sheet["A1"] = 1.23456789;
        view.Sheet.Cells["A2"].Formula = "1/7";
        view.Render(); // Populate the renderer's cache before changing the format.

        view.SelectColumn(0);
        Assert.That(view.Sheet.SelectionRange.Rows, Is.EqualTo(view.Sheet.RowCount));
        view.Decimals.SelectedItem = decimals;

        view.AssertDisplayed("A1", number);
        view.AssertDisplayed("A2", formula);
        Assert.That(view.Sheet["A1"], Is.EqualTo(1.23456789));
        Assert.That(view.Sheet.Cells["A2"].Formula, Is.EqualTo("1/7"));
        Assert.That(view.Sheet["A2"], Is.EqualTo(1.0 / 7.0));

        // Formatting a column also applies to its empty cells when values arrive later.
        view.Sheet["A25"] = 1.23456789;
        Assert.That(view.Sheet.Cells["A25"].DisplayText, Is.EqualTo(number));
    });

    [Test]
    public void SameDecimalChoiceCanBeAppliedToAnotherColumn() => InSpreadsheet(view =>
    {
        view.Sheet["A1"] = 1.23456789;
        view.Sheet["C1"] = 98.7654321;
        view.Render();
        view.SelectColumn(0);
        view.Decimals.SelectedItem = "2";

        view.SelectColumn(2);
        Assert.That(view.Decimals.SelectedItem, Is.EqualTo("General"));
        view.AssertDisplayed("C1", "98.7654321");
        view.Decimals.SelectedItem = "2";
        view.AssertDisplayed("C1", "98.77");

        view.SelectColumn(0);
        Assert.That(view.Decimals.SelectedItem, Is.EqualTo("2"));
        view.AssertDisplayed("A1", "1.23");
    });

    [Test]
    public void MixedColumnFormatsAreReportedAndCanBeUnified() => InSpreadsheet(view =>
    {
        view.Sheet["A1"] = 1.23456789;
        view.Sheet["A2"] = 98.7654321;
        view.Sheet.SetRangeDataFormat("A1", CellDataFormatFlag.Number,
            new NumberDataFormatter.NumberFormatArgs { DecimalPlaces = 2 });
        view.Render();

        view.SelectColumn(0);
        Assert.That(view.Decimals.SelectedIndex, Is.EqualTo(-1));
        Assert.That(view.Sheet.Cells["A2"].DataFormat, Is.EqualTo(CellDataFormatFlag.General));
        view.AssertDisplayed("A2", "98.7654321");

        view.Decimals.SelectedItem = "2";
        view.AssertDisplayed("A1", "1.23");
        view.AssertDisplayed("A2", "98.77");
        Assert.That(view.Decimals.SelectedItem, Is.EqualTo("2"));
    });

    [Test]
    public void GeneralAndRemovingAFormatRefreshTheRenderedText() => InSpreadsheet(view =>
    {
        view.Sheet["A1"] = 1.23456789;
        view.SelectColumn(0);
        view.Decimals.SelectedItem = "2";
        view.AssertDisplayed("A1", "1.23");

        view.Decimals.SelectedItem = "General";
        view.AssertDisplayed("A1", "1.23456789");
        view.Decimals.SelectedItem = "2";
        view.AssertDisplayed("A1", "1.23");
        view.Sheet.DeleteRangeDataFormat(view.Sheet.SelectionRange);
        view.AssertDisplayed("A1", "1.23456789");
    });

    [TestCase(1.0)]
    [TestCase(1.5)]
    public void DoubleClickingColumnDividerFitsContentsAndSupportsUndo(double scale) => InSpreadsheet(view =>
    {
        view.Sheet["B1"] = "Heat exchanger outlet temperature / 换热器出口温度";
        view.Render();
        var originalWidth = view.Sheet.ColumnHeaders[1].Width;

        view.DoubleClickColumnDivider(1);
        var fittedWidth = view.Sheet.ColumnHeaders[1].Width;
        Assert.That(fittedWidth, Is.GreaterThan(originalWidth));
        Assert.That(view.Panel.Grid.CanUndo(), Is.True);
        view.Panel.Grid.Undo();
        Assert.That(view.Sheet.ColumnHeaders[1].Width, Is.EqualTo(originalWidth));
        view.Panel.Grid.Redo();
        Assert.That(view.Sheet.ColumnHeaders[1].Width, Is.EqualTo(fittedWidth));

        view.Sheet["B1"] = "HX";
        view.Render();
        view.DoubleClickColumnDivider(1);
        Assert.That(view.Sheet.ColumnHeaders[1].Width, Is.InRange(1, fittedWidth - 1));
    }, scale);

    [Test]
    public void DoubleClickingACellStillStartsEditing() => InSpreadsheet(view =>
    {
        view.Sheet["A1"] = "editable";
        view.Click(new Point(view.Sheet.RowHeaderWidth + view.Sheet.ColumnHeaders[0].Width / 2.0,
            view.HeaderHeight + view.Sheet.RowHeaders[0].Height / 2.0), 2);
        Assert.That(view.Sheet.IsEditing, Is.True);
    });

    private sealed class SpreadsheetWindow : IDisposable
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Window _window;
        public SpreadsheetPanel Panel { get; }
        public Worksheet Sheet => Panel.Grid.CurrentWorksheet;
        public ComboBox Decimals { get; }
        public double HeaderHeight => Convert.ToDouble(typeof(Worksheet)
            .GetField("colHeaderHeight", InstanceFields)!.GetValue(Sheet));

        public SpreadsheetWindow(double scale)
        {
            var flowsheet = new DynamicRunner.Flowsheet(null, null);
            flowsheet.Init();
            Panel = new SpreadsheetPanel(flowsheet);
            Sheet.Resize(30, 8);
            Sheet.ScaleFactor = scale;
            var toolbar = new SpreadsheetToolbar(Panel, flowsheet);
            _window = new Window { Width = 1400, Height = 800, Content = toolbar };
            _window.Show();
            Render();
            Decimals = toolbar.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => c.Name == "DecimalPlaces");
        }

        public void SelectColumn(int column) => Click(new Point(
            (ColumnLeft(column) + Sheet.ColumnHeaders[column].Width / 2.0) * Sheet.ScaleFactor,
            HeaderHeight * Sheet.ScaleFactor / 2.0), 1);

        public void DoubleClickColumnDivider(int column) => Click(new Point(
            ColumnLeft(column + 1) * Sheet.ScaleFactor, HeaderHeight * Sheet.ScaleFactor / 2.0), 2);

        private double ColumnLeft(int column) => Sheet.RowHeaderWidth
            + Enumerable.Range(0, column).Sum(c => (double)Sheet.ColumnHeaders[c].Width);

        public void Click(Point local, int count)
        {
            var point = Panel.Grid.TranslatePoint(local, _window)!.Value;
            _window.MouseMove(point);
            for (int i = 0; i < count; i++)
            {
                _window.MouseDown(point, MouseButton.Left);
                _window.MouseUp(point, MouseButton.Left);
            }
            Dispatcher.UIThread.RunJobs();
        }

        public void Render()
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = _window.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null);
        }

        public void AssertDisplayed(string address, string expected)
        {
            Render();
            var cell = Sheet.Cells[address];
            Assert.That(cell.DisplayText, Is.EqualTo(expected), address + " formatted value");
            // DisplayText used to change while the FormattedText painted by Avalonia stayed stale.
            var rendered = (FormattedText?)typeof(Cell).GetField("formattedText", InstanceFields)!.GetValue(cell);
            Assert.That(rendered, Is.Not.Null);
            var paintedText = typeof(FormattedText).GetField("_text", InstanceFields)!.GetValue(rendered);
            Assert.That(paintedText, Is.EqualTo(expected), address + " rendered text");
        }

        public void Dispose() => _window.Close();
    }
}
