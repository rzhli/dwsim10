using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;
using unvell.ReoGrid;

namespace DWSIM.UI.Desktop.Avalonia.Tests;

[TestFixture, NonParallelizable]
public sealed class SpreadsheetFormulaInputTests
{
    private HeadlessUnitTestSession _session = null!;

    [OneTimeSetUp]
    public void StartSession() => _session = HeadlessUnitTestSession.StartNew(typeof(SpreadsheetTestApp));

    [OneTimeTearDown]
    public void StopSession() => _session.Dispose();

    private void InSpreadsheet(Action<FormulaWindow> test, double scale = 1) =>
        _session.Dispatch(() =>
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
                using var view = new FormulaWindow(scale);
                test(view);
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestCase(1.0)]
    [TestCase(1.5)]
    [TestCase(2.0)]
    public void ClickingCellsBuildsAFormulaInTheOriginalCell(double scale) => InSpreadsheet(view =>
    {
        view.ClickCell("D5");
        view.Type("=");
        view.ClickCell("B1");
        Assert.That(view.Sheet.IsEditing, Is.True);
        Assert.That(view.Sheet.CellEditText, Is.EqualTo("=B1"));
        Assert.That(view.Sheet.EditingCell.Position.ToAddress(), Is.EqualTo("D5"));
        view.Type("*");
        view.ClickCell("B5");
        Assert.That(view.Sheet.CellEditText, Is.EqualTo("=B1*B5"));
        view.Key(Key.Enter);

        view.AssertProduct();
        view.Panel.Grid.Undo();
        Assert.That(view.Sheet.GetCellData("D5"), Is.Null);
        view.Panel.Grid.Redo();
        view.AssertProduct();
    }, scale);

    [Test]
    public void FormulaBarCanPickReferencesWithoutLosingItsDraft() => InSpreadsheet(view =>
    {
        view.ClickCell("D5");
        view.FormulaBar.Focus();
        view.Type("=");
        view.ClickCell("B1");
        Assert.That(view.FormulaBar.IsFocused, Is.True);
        Assert.That(view.FormulaBar.Text, Is.EqualTo("=B1"));
        view.Type("*");
        view.ClickCell("B5");
        Assert.That(view.FormulaBar.Text, Is.EqualTo("=B1*B5"));
        view.Key(Key.Enter);
        view.AssertProduct();
    });

    [Test]
    public void DraggingCellsInsertsARangeAndAnotherClickReplacesIt() => InSpreadsheet(view =>
    {
        view.ClickCell("D5");
        view.Type("=SUM(");
        view.DragCells("B1", "B5");
        Assert.That(view.Sheet.CellEditText, Is.EqualTo("=SUM(B1:B5"));
        Assert.That(view.Sheet.SelectionRange, Is.EqualTo(new RangePosition("D5")));
        view.ClickCell("B5");
        Assert.That(view.Sheet.CellEditText, Is.EqualTo("=SUM(B5"));
        view.DragCells("B1", "B5");
        view.Type(")");
        view.Key(Key.Enter);
        Assert.That(view.Sheet.Cells["D5"].Formula, Is.EqualTo("SUM(B1:B5)"));
        Assert.That(view.Sheet["D5"], Is.EqualTo(181.5));
        Assert.That(view.Sheet.HighlightRanges, Is.Empty);
    });

    [Test]
    public void PickingAReferenceReplacesSelectedTextAndPreservesTheFormulaSuffix() => InSpreadsheet(view =>
    {
        view.ClickCell("D5");
        view.Type("=A1*B5");
        var editor = view.CellEditor;
        editor.SelectionStart = 1;
        editor.SelectionEnd = 3;
        view.ClickCell("B1");
        Assert.That(view.Sheet.CellEditText, Is.EqualTo("=B1*B5"));
        view.Key(Key.Enter);
        view.AssertProduct();
    });

    [Test]
    public void EscapeCancelsTheFormulaAndClearsReferenceHighlights() => InSpreadsheet(view =>
    {
        view.Sheet["D5"] = "keep this value";
        view.ClickCell("D5");
        view.Type("=");
        view.ClickCell("B1");
        view.Key(Key.Escape);
        Assert.That(view.Sheet.IsEditing, Is.False);
        Assert.That(view.Sheet["D5"], Is.EqualTo("keep this value"));
        Assert.That(view.Sheet.HighlightRanges, Is.Empty);
    });

    [TestCase("plain text", "plain text")]
    [TestCase("=\"literal B1\"", "literal B1")]
    public void ClickingAfterOrdinaryInputCommitsWithoutAddingAReference(string input, string expected) =>
        InSpreadsheet(view =>
        {
            view.ClickCell("D5");
            view.Type(input);
            view.ClickCell("B1");
            Assert.That(view.Sheet.IsEditing, Is.False);
            Assert.That(view.Sheet["D5"], Is.EqualTo(expected));
            Assert.That(view.Sheet["B1"], Is.EqualTo(180.16));
        });

    [Test]
    public void ClickingInsideTheCellEditorKeepsTheFormulaEditable() => InSpreadsheet(view =>
    {
        view.ClickCell("D5");
        view.Type("=B1*B5");
        view.ClickEditor();
        Assert.That(view.Sheet.IsEditing, Is.True);
        Assert.That(view.CellEditor.IsFocused, Is.True);
        Assert.That(view.Sheet.CellEditText, Is.EqualTo("=B1*B5"));
        view.Key(Key.Enter);
        view.AssertProduct();
    });

    private sealed class FormulaWindow : IDisposable
    {
        private readonly Window _window;
        public SpreadsheetPanel Panel { get; }
        public Worksheet Sheet => Panel.Grid.CurrentWorksheet;
        public TextBox FormulaBar { get; }
        public TextBox CellEditor => Panel.Grid.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.GetType().Name == "InputTextBox");

        public FormulaWindow(double scale)
        {
            var flowsheet = new DynamicRunner.Flowsheet(null, null);
            flowsheet.Init();
            Panel = new SpreadsheetPanel(flowsheet);
            Sheet.Resize(30, 8);
            Sheet.ScaleFactor = scale;
            Sheet["B1"] = 180.16;
            Sheet["B5"] = 1.34;
            var toolbar = new SpreadsheetToolbar(Panel, flowsheet);
            _window = new Window { Width = 1400, Height = 1000, Content = toolbar };
            _window.Show();
            Pump();
            FormulaBar = toolbar.GetVisualDescendants().OfType<TextBox>()
                .Single(t => Equals(t.Watermark, "Value or formula of the selected cell"));
        }

        private Point CellPoint(string address)
        {
            var pos = new CellPosition(address);
            var headerHeight = Convert.ToDouble(typeof(Worksheet)
                .GetField("colHeaderHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Sheet));
            var local = new Point(
                (Sheet.RowHeaderWidth + Enumerable.Range(0, pos.Col).Sum(c => (double)Sheet.ColumnHeaders[c].Width)
                    + Sheet.ColumnHeaders[pos.Col].Width / 2.0) * Sheet.ScaleFactor,
                (headerHeight + Enumerable.Range(0, pos.Row).Sum(r => (double)Sheet.RowHeaders[r].Height)
                    + Sheet.RowHeaders[pos.Row].Height / 2.0) * Sheet.ScaleFactor);
            return Panel.Grid.TranslatePoint(local, _window)!.Value;
        }

        public void ClickCell(string address) => Click(CellPoint(address));

        public void ClickEditor() => Click(CellEditor.TranslatePoint(new Point(6, 6), _window)!.Value);

        private void Click(Point point)
        {
            _window.MouseMove(point);
            _window.MouseDown(point, MouseButton.Left);
            _window.MouseUp(point, MouseButton.Left);
            Pump();
        }

        public void DragCells(string from, string to)
        {
            _window.MouseMove(CellPoint(from));
            _window.MouseDown(CellPoint(from), MouseButton.Left);
            _window.MouseMove(CellPoint(to));
            _window.MouseUp(CellPoint(to), MouseButton.Left);
            Pump();
        }

        public void Type(string text) { _window.KeyTextInput(text); Pump(); }

        public void Key(Key key)
        {
            _window.KeyPress(key, RawInputModifiers.None);
            _window.KeyRelease(key, RawInputModifiers.None);
            Pump();
        }

        private void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = _window.CaptureRenderedFrame();
        }

        public void AssertProduct()
        {
            Assert.That(Sheet.Cells["D5"].Formula, Is.EqualTo("B1*B5"));
            Assert.That(Sheet["D5"], Is.EqualTo(241.4144).Within(1e-10));
            Assert.That(Sheet["B1"], Is.EqualTo(180.16));
            Assert.That(Sheet["B5"], Is.EqualTo(1.34));
        }

        public void Dispose() => _window.Close();
    }
}
