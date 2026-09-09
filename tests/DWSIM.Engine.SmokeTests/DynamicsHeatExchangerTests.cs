using System;
using System.Linq;
using DWSIM.Interfaces.Enums;
using DWSIM.Thermodynamics.PropertyPackages;
using DWSIM.Thermodynamics.Streams;
using DWSIM.UnitOperations.UnitOperations;
using NUnit.Framework;
using ObjectType = DWSIM.Interfaces.Enums.GraphicObjects.ObjectType;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class DynamicsHeatExchangerTests
    {
        [OneTimeSetUp]
        public void Setup()
        {
            DWSIM.GlobalSettings.Settings.AutomationMode = true;
            DWSIM.GlobalSettings.Settings.InspectorEnabled = false;
            DWSIM.GlobalSettings.Settings.CultureInfo = "en";
            FlowsheetBase.FlowsheetBase.AddPropPacks();
        }

        private static (HeatExchanger hx, MaterialStream hot, MaterialStream cold,
            MaterialStream hotOut, MaterialStream coldOut) Exchanger(bool steam, double step = 1.0, double coldPressure = 200000.0)
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            fs.AddCompound("Water");
            var pp = new SteamTablesPropertyPackage { Flowsheet = fs };
            fs.AddPropertyPackage(pp);

            MaterialStream Stream(string tag, double temperature, double pressure = 200000.0)
            {
                var ms = (MaterialStream)fs.AddObject(ObjectType.MaterialStream, 0, 0, tag);
                ms.SetFlowsheet(fs);
                ms.PropertyPackage = pp;
                ms.SetOverallComposition(new[] { 1.0 });
                ms.SetPressure(pressure);
                ms.SetTemperature(temperature);
                ms.SetMassFlow(1.0);
                ms.SetFlashSpec("PT");
                ms.Calculate();
                ms.SetMassFlow(0.0);
                return ms;
            }

            var hot = Stream("hot", steam ? 393.5 : 353.15);
            var cold = Stream("cold", 303.15, coldPressure);
            var hotOut = Stream("hot outlet", 353.15);
            var coldOut = Stream("cold outlet", 303.15, coldPressure);
            var hx = (HeatExchanger)fs.AddObject(ObjectType.HeatExchanger, 0, 0, "HX");
            hx.SetFlowsheet(fs);
            hx.PropertyPackage = pp;
            fs.ConnectObjects(hot.GraphicObject, hx.GraphicObject, 0, 0);
            fs.ConnectObjects(cold.GraphicObject, hx.GraphicObject, 0, 1);
            fs.ConnectObjects(hx.GraphicObject, hotOut.GraphicObject, 0, 0);
            fs.ConnectObjects(hx.GraphicObject, coldOut.GraphicObject, 1, 0);
            hx.CalculationMode = HeatExchangerCalcMode.CalcBothTemp_UA;
            hx.Area = 2.0;
            hx.OverallCoefficient = 2000.0;
            hx.SetDynamicProperty("Minimum Pressure", 200000.0);
            hx.SetDynamicProperty("Number of Cells", 1);
            hx.SetDynamicProperty("Substeps", 1);
            hx.SetDynamicProperty("Wall Thermal Mass", 0.0);
            hx.SetDynamicProperty("Initialize using Inlet Streams", true);
            hx.SetDynamicProperty("Reset Contents", false);

            // Closed holdups isolate the heat-transfer step from inlet/outlet energy transport.
            var hotCell = (MaterialStream)hot.CloneXML();
            var coldCell = (MaterialStream)cold.CloneXML();
            hotCell.SetMassFlow(1.0);
            coldCell.SetMassFlow(steam ? 1000.0 : 1.0);
            hx.AccumulationStreamsHot.Add(hotCell);
            hx.AccumulationStreamsCold.Add(coldCell);
            hx.SetDynamicProperty("Volume for Hot Fluid", 1.0 / hot.Phases[0].Properties.density.Value);
            hx.SetDynamicProperty("Volume for Cold Fluid", coldCell.GetMassFlow() / cold.Phases[0].Properties.density.Value);

            var integrator = new DWSIM.DynamicsManager.Integrator
            {
                ID = "integrator", IntegrationStep = TimeSpan.FromSeconds(step),
                ShouldCalculateEquilibrium = true, ShouldCalculatePressureFlow = true
            };
            var schedule = new DWSIM.DynamicsManager.Schedule { ID = "schedule", CurrentIntegrator = integrator.ID };
            fs.DynamicsManager.IntegratorList.Add(integrator.ID, integrator);
            fs.DynamicsManager.ScheduleList.Add(schedule.ID, schedule);
            fs.DynamicsManager.CurrentSchedule = schedule.ID;
            fs.DynamicMode = true;
            return (hx, hot, cold, hotOut, coldOut);
        }

        [TestCase(200000.0)]
        [TestCase(100000000.0)]
        public void CondensingSteamTransfersLatentHeatWithoutAnArtificialSensibleHeatLimit(double coldPressure)
        {
            var (hx, _, _, _, _) = Exchanger(steam: true, coldPressure: coldPressure);
            double hotEnergy = hx.AccumulationStreamsHot[0].GetMassEnthalpy();
            double coldEnergy = 1000.0 * hx.AccumulationStreamsCold[0].GetMassEnthalpy();

            hx.RunDynamicModel();

            var hot = hx.AccumulationStreamsHot[0];
            var cold = hx.AccumulationStreamsCold[0];
            // UA = 4 kW/K, with condensing steam near 120 C and 1000 kg of water near 30 C.
            Assert.That(hx.Q.Value, Is.InRange(350.0, 365.0));
            Assert.That(hot.Phases[2].Properties.massfraction.Value, Is.InRange(0.75, 0.95));
            Assert.That(hotEnergy - hot.GetMassEnthalpy(), Is.EqualTo(hx.Q.Value).Within(0.01));
            Assert.That(1000.0 * cold.GetMassEnthalpy() - coldEnergy, Is.EqualTo(hx.Q.Value).Within(0.01));
        }

        [TestCase(1.0)]
        [TestCase(10.0)]
        public void SensibleHeatTransferConservesEnergyAndDoesNotCrossTemperatures(double step)
        {
            var (hx, _, _, _, _) = Exchanger(steam: false, step: step);
            double initialEnergy = hx.AccumulationStreamsHot[0].GetMassEnthalpy() + hx.AccumulationStreamsCold[0].GetMassEnthalpy();
            double initialColdEnthalpy = hx.AccumulationStreamsCold[0].GetMassEnthalpy();

            hx.RunDynamicModel();

            var hot = hx.AccumulationStreamsHot[0];
            var cold = hx.AccumulationStreamsCold[0];
            Assert.That(hot.GetTemperature(), Is.GreaterThanOrEqualTo(cold.GetTemperature()));
            Assert.That(cold.GetTemperature(), Is.GreaterThan(303.15));
            Assert.That(hot.GetMassEnthalpy() + cold.GetMassEnthalpy(), Is.EqualTo(initialEnergy).Within(0.01));
            Assert.That(hx.Q.Value * step, Is.EqualTo(cold.GetMassEnthalpy() - initialColdEnthalpy).Within(0.01));
        }

        [Test]
        public void DrainingUsesTheCellCompositionEvenIfTheSavedOutletIsInvalid()
        {
            var (hx, hot, cold, hotOut, coldOut) = Exchanger(steam: false);
            hx.OverallCoefficient = 0.0;
            hot.SetMassFlow(0.1);
            cold.SetMassFlow(0.1);
            foreach (var outlet in new[] { hotOut, coldOut })
            {
                outlet.SetMassFlow(0.1);
                var water = outlet.Phases[0].Compounds["Water"];
                water.MassFlow = double.NaN;
                water.MolarFlow = double.NaN;
                water.MoleFraction = double.NaN;
            }

            hx.RunDynamicModel();

            foreach (var cell in hx.AccumulationStreamsHot.Concat(hx.AccumulationStreamsCold))
            {
                Assert.That(cell.GetMassFlow(), Is.EqualTo(1.0).Within(1e-9));
                Assert.That(double.IsFinite(cell.GetMassEnthalpy()), Is.True);
                Assert.That(cell.Phases[0].Compounds["Water"].MoleFraction, Is.EqualTo(1.0));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AFreeOutletFollowsInletFlowUnlessItsDischargeIsExplicitlySpecified(bool specifiedFlow)
        {
            var (hx, hot, _, hotOut, _) = Exchanger(steam: false);
            hx.OverallCoefficient = 0.0;
            hot.SetMassFlow(0.2);
            hotOut.SetMassFlow(0.1);
            hotOut.DynamicsSpec = specifiedFlow
                ? DWSIM.Interfaces.Enums.Dynamics.DynamicsSpecType.Flow
                : DWSIM.Interfaces.Enums.Dynamics.DynamicsSpecType.Pressure;

            hx.RunDynamicModel();

            Assert.That(hotOut.GetMassFlow(), Is.EqualTo(specifiedFlow ? 0.1 : 0.2).Within(1e-9));
            Assert.That(hx.AccumulationStreamsHot[0].GetMassFlow(), Is.EqualTo(specifiedFlow ? 1.1 : 1.0).Within(1e-9));

            hot.SetMassFlow(0.05);
            hx.RunDynamicModel();

            Assert.That(hotOut.GetMassFlow(), Is.EqualTo(specifiedFlow ? 0.1 : 0.05).Within(1e-9));
            Assert.That(hx.AccumulationStreamsHot[0].GetMassFlow(), Is.EqualTo(specifiedFlow ? 1.05 : 1.0).Within(1e-9));
        }

        [TestCase(1)]
        [TestCase(5)]
        public void FrictionalPressureDropDoesNotAccumulateAcrossTimeSteps(int substeps)
        {
            var (hx, hot, _, hotOut, _) = Exchanger(steam: false);
            hx.OverallCoefficient = 0.0;
            hx.SetDynamicProperty("Minimum Pressure", 101325.0);
            hx.SetDynamicProperty("Substeps", substeps);
            hot.SetMassFlow(0.2);

            for (int i = 0; i < 10; i++) hx.RunDynamicModel();

            Assert.That(hot.GetPressure(), Is.EqualTo(200000.0).Within(1e-6));
            Assert.That(hotOut.GetPressure(), Is.EqualTo(200000.0 - 0.2 * 0.2).Within(1e-6));
            Assert.That(hx.AccumulationStreamsHot[0].GetMassFlow(), Is.EqualTo(1.0).Within(1e-9));
        }

        [Test]
        public void ResetDiscardsThePreviousInventoryPressureHistory()
        {
            var (hx, _, cold, _, coldOut) = Exchanger(steam: false);
            hx.OverallCoefficient = 0.0;
            hx.RunDynamicModel();

            cold.SetTemperature(313.15);
            cold.SetMassFlow(1.0);
            cold.SetFlashSpec("PT");
            cold.Calculate();
            cold.SetMassFlow(0.0);
            hx.SetDynamicProperty("Reset Contents", true);

            hx.RunDynamicModel();

            Assert.That(coldOut.GetTemperature(), Is.EqualTo(313.15).Within(0.001));
            Assert.That(coldOut.GetPressure(), Is.EqualTo(200000.0).Within(1e-6));
        }
    }
}
