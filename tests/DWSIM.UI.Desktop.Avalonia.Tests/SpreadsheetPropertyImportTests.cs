using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DWSIM.Interfaces;
using NUnit.Framework;
using ObjectType = DWSIM.Interfaces.Enums.GraphicObjects.ObjectType;
using Units = DWSIM.SharedClasses.SystemsOfUnits;

namespace DWSIM.UI.Desktop.Avalonia.Tests;

[TestFixture, NonParallelizable]
public sealed class SpreadsheetPropertyImportTests
{
    private HeadlessUnitTestSession _session = null!;

    [OneTimeSetUp]
    public void StartSession() => _session = HeadlessUnitTestSession.StartNew(typeof(SpreadsheetTestApp));

    [OneTimeTearDown]
    public void StopSession() => _session.Dispose();

    private void InSpreadsheet(Func<ImportWindow, Task> test) =>
        _session.Dispatch(async () =>
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
                using var view = new ImportWindow();
                await test(view);
                return true;
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestCase(239, "mol/L", 2.5)]
    [TestCase(240, "mol/L", 2.5)]
    [TestCase(241, "mol/L", 2.5)]
    [TestCase(242, "kmol/m3", 2.5)]
    [TestCase(243, "mol/m3", 2500.0)]
    [TestCase(244, "mol/mL", 0.0025)]
    [TestCase(245, "mol/cm3", 0.0025)]
    [TestCase(239, "lbmol/ft3", 2500.0 * 0.028316846592 / 453.59237)]
    public void ImportUsesTheSelectedMolarConcentrationUnit(int property, string unit, double expected) =>
        InSpreadsheet(async view =>
        {
            var (dialog, import) = view.BeginImport();
            var lists = Lists(dialog);
            Select(lists[0], view.Stream.Name);
            Select(lists[1], $"PROP_MS_{property}/Glucose");

            Assert.That(lists[2].Items.Cast<ListBoxItem>().Select(i => i.Tag), Is.EquivalentTo(
                new[] { "mol/m3", "kmol/m3", "mol/L", "mol/cm3", "mol/mL", "lbmol/ft3" }));
            Assert.That(dialog.SelectedUnit, Is.EqualTo("kmol/m3"), "Use the flowsheet's default unit.");
            Select(lists[2], unit);
            Confirm(dialog);
            await import;

            var cell = view.Panel.Grid.CurrentWorksheet.Cells["A1"];
            Assert.That(cell.Formula, Is.EqualTo(
                $"GETPROPVAL(\"{view.Stream.Name}\",\"PROP_MS_{property}/Glucose\",\"{unit}\")"));
            Assert.That(cell.Data, Is.EqualTo(expected).Within(Math.Abs(expected) * 1e-5));
            Assert.That(Units.Converter.ConvertToSI(unit, expected), Is.EqualTo(2500.0).Within(0.025),
                "The chosen unit must also convert back correctly when exporting data.");
        });

    [Test]
    public void SwitchingPropertiesRefreshesUnitsAndClearsThemForDimensionlessValues() =>
        InSpreadsheet(async view =>
        {
            var (dialog, import) = view.BeginImport();
            var lists = Lists(dialog);
            Select(lists[0], view.Stream.Name);
            Select(lists[1], "PROP_MS_239/Glucose");
            Select(lists[2], "mol/L");

            Select(lists[1], "PROP_MS_0"); // Temperature
            Assert.That(lists[2].Items.Cast<ListBoxItem>().Select(i => i.Tag),
                Is.EquivalentTo(new[] { "K", "R", "C", "F" }));
            Assert.That(dialog.SelectedUnit, Is.EqualTo("C"));

            Select(lists[1], "PROP_MS_27"); // Vapor mole fraction
            Assert.That(lists[2].Items, Is.Empty);
            Assert.That(dialog.SelectedUnit, Is.Empty);
            Confirm(dialog);
            await import;

            var cell = view.Panel.Grid.CurrentWorksheet.Cells["A1"];
            Assert.That(cell.Formula, Is.EqualTo($"GETPROPVAL(\"{view.Stream.Name}\",\"PROP_MS_27\")"));
            Assert.That(cell.Data, Is.EqualTo(0.25));
        });

    [Test]
    public void ChangingObjectsRequiresAPropertySelectionBeforeImporting() =>
        InSpreadsheet(async view =>
        {
            var (dialog, import) = view.BeginImport();
            var lists = Lists(dialog);
            Select(lists[0], view.Stream.Name);
            Select(lists[1], "PROP_MS_239/Glucose");
            Select(lists[2], "mol/L");

            lists[0].SelectedItem = lists[0].Items.Cast<ListBoxItem>()
                .Single(i => !Equals(i.Tag, view.Stream.Name));
            Assert.That(dialog.SelectedPropertyKey, Is.Null);
            Assert.That(dialog.SelectedUnit, Is.Empty);
            Confirm(dialog);
            await import;

            Assert.That(view.Panel.Grid.CurrentWorksheet.GetCellData("A1"), Is.Null);
        });

    private static ListBox[] Lists(PropertySelectorDialog dialog)
    {
        Dispatcher.UIThread.RunJobs();
        return dialog.GetVisualDescendants().OfType<ListBox>().ToArray();
    }

    private static void Select(ListBox list, string key) =>
        list.SelectedItem = list.Items.Cast<ListBoxItem>().Single(i => Equals(i.Tag, key));

    private static void Confirm(PropertySelectorDialog dialog) =>
        dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "OK"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class ImportWindow : IDisposable
    {
        private readonly Window _window;
        private readonly SpreadsheetToolbar _toolbar;
        public ISimulationObject Stream { get; }
        public SpreadsheetPanel Panel { get; }

        public ImportWindow()
        {
            var flowsheet = new DynamicRunner.Flowsheet(null, null);
            flowsheet.Init();
            flowsheet.AddCompound("Glucose");
            flowsheet.FlowsheetOptions.SelectedUnitSystem = new Units.SI
            {
                molar_conc = "kmol/m3",
                temperature = "C"
            };
            Stream = flowsheet.AddObject(ObjectType.MaterialStream, 0, 0, "Feed");
            flowsheet.AddObject(ObjectType.MaterialStream, 100, 0, "Other Stream");
            var materialStream = (IMaterialStream)Stream;
            foreach (var phase in materialStream.Phases.Values)
                phase.Compounds["Glucose"].Molarity = 2500.0; // mol/m3
            materialStream.Phases[2].Properties.molarfraction = 0.25;

            Panel = new SpreadsheetPanel(flowsheet);
            Panel.Grid.CurrentWorksheet.Resize(10, 8);
            _toolbar = new SpreadsheetToolbar(Panel, flowsheet);
            _window = new Window { Width = 1200, Height = 800, Content = _toolbar };
            _window.Show();
        }

        public (PropertySelectorDialog Dialog, Task Import) BeginImport()
        {
            var import = (Task)typeof(SpreadsheetToolbar)
                .GetMethod("ImportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_toolbar, null)!;
            return (_window.OwnedWindows.OfType<PropertySelectorDialog>().Single(), import);
        }

        public void Dispose()
        {
            foreach (var dialog in _window.OwnedWindows.ToArray()) dialog.Close();
            _window.Close();
        }
    }
}
