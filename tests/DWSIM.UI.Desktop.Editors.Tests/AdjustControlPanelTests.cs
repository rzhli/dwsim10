using System;
using DWSIM.Thermodynamics.PropertyPackages;
using DWSIM.Thermodynamics.Streams;
using DWSIM.UnitOperations.SpecialOps;
using DWSIM.UnitOperations.UnitOperations;
using NUnit.Framework;
using ObjectType = DWSIM.Interfaces.Enums.GraphicObjects.ObjectType;

namespace DWSIM.UI.Desktop.Editors.Tests
{
    [TestFixture, NonParallelizable]
    public class AdjustControlPanelTests
    {
        [OneTimeSetUp]
        public void SetUpOnce()
        {
            GlobalSettings.Settings.AutomationMode = true;
            GlobalSettings.Settings.InspectorEnabled = false;
            GlobalSettings.Settings.EnableParallelProcessing = false;
            GlobalSettings.Settings.CalculatorActivated = true;
            GlobalSettings.Settings.SolverMode = 1;
            GlobalSettings.Settings.CultureInfo = "en";
            FlowsheetBase.FlowsheetBase.AddPropPacks();
        }

        private static (Adjust Adjust, Heater Heater, MaterialStream Feed, MaterialStream Outlet)
            HeaterControl(int method = 1, string temperatureUnit = "C")
        {
            var fs = (FlowsheetBase.FlowsheetBase)new DWSIM.Automation.Automation3().CreateFlowsheet();
            fs.AddCompound("Water");
            fs.FlowsheetOptions.SelectedUnitSystem = new DWSIM.SharedClasses.SystemsOfUnits.SI_ENG();
            fs.FlowsheetOptions.SelectedUnitSystem.temperature = temperatureUnit;
            var pp = new SteamTablesPropertyPackage { Flowsheet = fs };
            fs.AddPropertyPackage(pp);

            MaterialStream Stream(string tag)
            {
                var stream = (MaterialStream)fs.AddObject(ObjectType.MaterialStream, 0, 0, tag);
                stream.PropertyPackage = pp;
                stream.SetOverallComposition(new[] { 1.0 });
                stream.SetTemperature(303.15);
                stream.SetPressure(200000);
                stream.SetMassFlow(1.0);
                stream.SetFlashSpec("PT");
                return stream;
            }

            var feed = Stream("feed");
            var outlet = Stream("outlet");
            var heater = (Heater)fs.AddObject(ObjectType.Heater, 50, 0, "heater");
            heater.PropertyPackage = pp;
            heater.CalcMode = Heater.CalculationMode.HeatAdded;
            heater.DeltaQ = 50.0;
            heater.DeltaP = 0.0;
            heater.Eficiencia = 100.0;
            fs.ConnectObjects(feed.GraphicObject, heater.GraphicObject, 0, 0);
            fs.ConnectObjects(heater.GraphicObject, outlet.GraphicObject, 0, 0);

            var adjust = (Adjust)fs.AddObject(ObjectType.OT_Adjust, 100, 0, "C-1");
            adjust.ManipulatedObject = heater;
            adjust.ManipulatedObjectData.ID = heater.Name;
            adjust.ManipulatedObjectData.PropertyName = "PROP_HT_3";
            adjust.ControlledObject = outlet;
            adjust.ControlledObjectData.ID = outlet.Name;
            adjust.ControlledObjectData.PropertyName = "PROP_MS_0";
            adjust.AdjustValue = 333.15;
            adjust.Tolerance = 0.1;
            adjust.MinVal = 0;
            adjust.MaxVal = 250;
            adjust.MaximumIterations = 100;
            adjust.SolvingMethodSelf = method;
            adjust.SimultaneousAdjust = false;
            return (adjust, heater, feed, outlet);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryMethodLeavesTheAppliedSolutionWithinTheTemperatureTolerance(int method)
        {
            var (adjust, heater, _, outlet) = HeaterControl(method);

            var solution = AdjustControlPanel.Solve(adjust);

            Assert.That(outlet.GetTemperature() - 273.15, Is.EqualTo(60.0).Within(0.1));
            Assert.That(heater.DeltaQ.Value, Is.EqualTo(solution));
        }

        [Test]
        public void FahrenheitToleranceIsATemperatureDifference()
        {
            var (adjust, _, _, outlet) = HeaterControl(temperatureUnit: "F");

            AdjustControlPanel.Solve(adjust);

            Assert.That(outlet.GetTemperature(), Is.EqualTo(333.15).Within(0.1 * 5.0 / 9.0));
        }

        [Test]
        public void SecantCanStartWithZeroHeatDuty()
        {
            var (adjust, heater, _, outlet) = HeaterControl(method: 0);
            heater.DeltaQ = 0;

            AdjustControlPanel.Solve(adjust);

            Assert.That(outlet.GetTemperature(), Is.EqualTo(333.15).Within(0.1));
        }

        [Test]
        public void ReferencedTemperatureSetPointUsesAnOffset()
        {
            var (adjust, _, feed, outlet) = HeaterControl();
            adjust.Referenced = true;
            adjust.ReferencedObjectData.ID = feed.Name;
            adjust.ReferencedObjectData.PropertyName = "PROP_MS_0";
            adjust.AdjustValue = 30.0;

            AdjustControlPanel.Solve(adjust);

            Assert.That(outlet.GetTemperature() - feed.GetTemperature(), Is.EqualTo(30.0).Within(0.1));
        }

        [Test]
        public void MassFlowAdjustmentDoesNotUseTemperatureToleranceAsFlowAccuracy()
        {
            var (adjust, _, feed, outlet) = HeaterControl();
            adjust.ManipulatedObject = feed;
            adjust.ManipulatedObjectData.ID = feed.Name;
            adjust.ManipulatedObjectData.PropertyName = "PROP_MS_2";
            adjust.MinVal = 1000.0; // kg/h, whereas the root finder works in kg/s.
            adjust.MaxVal = 2000.0;

            AdjustControlPanel.Solve(adjust);

            Assert.That(outlet.GetTemperature(), Is.EqualTo(333.15).Within(0.1));
            Assert.That(feed.GetMassFlow() * 3600.0, Is.InRange(1000.0, 2000.0));
        }

        [Test]
        public void IpoptCannotReportSuccessAtAnUnreachableTemperature()
        {
            var (adjust, heater, _, _) = HeaterControl(method: 3);
            heater.DeltaQ = 10;
            adjust.MaxVal = 25; // Insufficient heat to reach 60 C from 30 C at 1 kg/s.

            Assert.That(() => AdjustControlPanel.Solve(adjust),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Target not reached"));
        }

        [Test]
        public void CalculationFailureCannotBeTreatedAsAValidControlIteration()
        {
            var (adjust, _, feed, _) = HeaterControl();
            feed.SetTemperature(-1.0);

            Assert.That(() => AdjustControlPanel.Solve(adjust),
                Throws.TypeOf<AggregateException>().With.Message.Contains("Flowsheet calculation failed"));
        }
    }
}
