//    A steam valve reducing pressure in a dynamic run, on the IAPWS-IF97 steam tables.
//
//    Two separate defects in Valve.RunDynamicModel made the outlet-pressure calculation fail for
//    a plain steam let-down, both of them unit or state-point mistakes rather than anything
//    physical about the valve:
//
//    1. Kv_General / Kv_Gas asked the property package for the gas density at (273.15 K,
//       101325 Pa) and used it as the normal density in the 519-coefficient IEC 60534 gas
//       equation. That state point is not a vapour for water, and the steam tables answer it with
//       the density of SATURATED steam at 0 degC - the value at 611 Pa, 0.0049 kg/m3, where the
//       equation wants the normal density of 0.804 kg/Nm3. Being 170x low shrank the apparent
//       choked-flow limit by sqrt(170) ~ 13x, so the quadratic for P2 had a negative
//       discriminant, quadForm returned (NaN, NaN), the two "root > 0" tests both went false on
//       NaN, and the model threw "Unable to calculate the outlet pressure" for a duty an order of
//       magnitude short of choking.
//
//    2. Kv_Steam iterates P2 in bar, but handed that number to AUX_VAPDENS, which takes pressure
//       in Pa. 2.1 bar was read as 2.1 Pa, i.e. 2.1e-5 bar; the density came back 1.7e5x low, the
//       specific volume that large, and the first iteration pushed P2 to -801 bar. The second
//       asked the steam tables for the density at a negative pressure and got their out-of-range
//       refusal.
//
//    Both were reached by the same flowsheet: 3 bar saturated steam, 300 kg/h, through a valve of
//    Kv 100. Nothing there is near a real limit - the true choked-flow limit at 3 bar is about
//    3400 kg/h - so every case here asserts a solved subsonic outlet pressure just below the
//    inlet, not merely that no exception escaped.

using System;
using System.Linq;
using NUnit.Framework;
using DWSIM.DynamicsManager;
using Valve = DWSIM.UnitOperations.UnitOperations.Valve;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class ValveSteamOutletPressureTests
    {
        // 3 bar saturated steam: the supply the reported flowsheet ran on.
        private const double InletPressure = 300000.0;
        private const double MassFlow = 300.0 / 3600.0;   // 300 kg/h
        private const double Kv = 100.0;

        [OneTimeSetUp]
        public void Setup()
        {
            DWSIM.GlobalSettings.Settings.AutomationMode = true;
            DWSIM.GlobalSettings.Settings.InspectorEnabled = false;
            DWSIM.GlobalSettings.Settings.CultureInfo = "en";

            FlowsheetBase.FlowsheetBase.AddPropPacks();
        }

        /// <summary>
        /// Inlet specified by pressure, outlet by flow: the specification pair that makes the valve
        /// back-calculate its own outlet pressure, which is where both defects lived.
        /// </summary>
        private static (DWSIM.DynamicRunner.Flowsheet fs, Valve valve,
                        DWSIM.Thermodynamics.Streams.MaterialStream inlet,
                        DWSIM.Thermodynamics.Streams.MaterialStream outlet)
            Build(Valve.CalculationMode mode)
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            fs.AddCompound("Water");

            var pp = new DWSIM.Thermodynamics.PropertyPackages.SteamTablesPropertyPackage
            {
                Flowsheet = fs
            };
            fs.AddPropertyPackage(pp);

            var inletObj = fs.AddObject(
                DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.MaterialStream, 0, 0, "steam");
            var inlet = (DWSIM.Thermodynamics.Streams.MaterialStream)fs.SimulationObjects[inletObj.Name];
            inlet.SetFlowsheet(fs);
            inlet.SetPropertyPackage(pp);
            inlet.SetPressure(InletPressure);
            inlet.SetMassFlow(MassFlow);

            // Dry saturated steam at the inlet pressure: vapour fraction 1, so the model takes its
            // single-phase gas branch.
            inlet.SpecType = DWSIM.Interfaces.Enums.StreamSpec.Pressure_and_VaporFraction;
            inlet.Phases[2].Properties.molarfraction = 1.0;
            inlet.Calculate();

            var outletObj = fs.AddObject(
                DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.MaterialStream, 200, 0, "let-down");
            var outlet = (DWSIM.Thermodynamics.Streams.MaterialStream)fs.SimulationObjects[outletObj.Name];
            outlet.SetFlowsheet(fs);
            outlet.SetPropertyPackage(pp);
            outlet.SetPressure(InletPressure);
            outlet.SetMassFlow(MassFlow);

            var valveObj = fs.AddObject(
                DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.Valve, 100, 0, "VALVE-1");
            var valve = (Valve)fs.SimulationObjects[valveObj.Name];
            valve.SetFlowsheet(fs);
            valve.PropertyPackage = pp;
            valve.CalcMode = mode;
            valve.Kv = Kv;
            valve.EnableOpeningKvRelationship = true;
            valve.DefinedOpeningKvRelationShipType = Valve.OpeningKvRelationshipType.Linear;
            valve.OpeningPct = 100.0;

            fs.ConnectObjects(inlet.GraphicObject, valve.GraphicObject, 0, 0);
            fs.ConnectObjects(valve.GraphicObject, outlet.GraphicObject, 0, 0);

            inlet.DynamicsSpec = DWSIM.Interfaces.Enums.Dynamics.DynamicsSpecType.Pressure;
            outlet.DynamicsSpec = DWSIM.Interfaces.Enums.Dynamics.DynamicsSpecType.Flow;

            AddSchedule(fs);

            return (fs, valve, inlet, outlet);
        }

        /// <summary>
        /// Every dynamic model looks its integrator up through the manager's current schedule, so a
        /// flowsheet built in code has to carry one before RunDynamicModel can be called at all.
        /// </summary>
        private static void AddSchedule(DWSIM.DynamicRunner.Flowsheet fs)
        {
            var manager = fs.DynamicsManager;

            var integrator = new Integrator
            {
                ID = Guid.NewGuid().ToString(),
                Description = "integrator",
                IntegrationStep = TimeSpan.FromSeconds(1.0),
                Duration = TimeSpan.FromSeconds(10.0),
                ShouldCalculatePressureFlow = true,
                ShouldCalculateControl = true,
                ShouldCalculateEquilibrium = true
            };
            manager.IntegratorList.Add(integrator.ID, integrator);

            var schedule = new Schedule
            {
                ID = Guid.NewGuid().ToString(),
                Description = "schedule",
                CurrentIntegrator = integrator.ID,
                UseCurrentStateAsInitial = true
            };
            manager.ScheduleList.Add(schedule.ID, schedule);
            manager.CurrentSchedule = schedule.ID;
        }

        /// <summary>
        /// The steam tables' answer at the state point the gas equation's normal density used to be
        /// read from. Pinned so that a change in the property package cannot quietly restore the
        /// premise the old code rested on.
        /// </summary>
        [Test]
        public void TheSteamTablesDoNotGiveTheNormalDensityAtZeroCelsius()
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            fs.AddCompound("Water");

            var pp = new DWSIM.Thermodynamics.PropertyPackages.SteamTablesPropertyPackage
            {
                Flowsheet = fs
            };

            var obj = fs.AddObject(
                DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.MaterialStream, 0, 0, "s");
            var ms = (DWSIM.Thermodynamics.Streams.MaterialStream)fs.SimulationObjects[obj.Name];
            ms.SetFlowsheet(fs);
            ms.SetPropertyPackage(pp);
            pp.CurrentMaterialStream = ms;

            var atZeroCelsius = pp.AUX_VAPDENS(273.15, 101325.0);
            var normal = 18.01528 / 22.414;

            // Saturated steam at 0 degC sits at 611 Pa, hence ~0.0049 kg/m3 against 0.804 kg/Nm3.
            Assert.That(atZeroCelsius, Is.LessThan(normal / 100.0),
                        "AUX_VAPDENS(273.15, 101325) is the saturated-vapour density at 0 degC, " +
                        "not the normal density the IEC gas equations take");
        }

        [TestCase(Valve.CalculationMode.Kv_General)]
        [TestCase(Valve.CalculationMode.Kv_Gas)]
        [TestCase(Valve.CalculationMode.Kv_Steam)]
        public void ASteamValveResolvesItsOutletPressure(Valve.CalculationMode mode)
        {
            var (_, valve, inlet, outlet) = Build(mode);

            Assert.That(() => valve.RunDynamicModel(), Throws.Nothing,
                        $"{mode} threw on 300 kg/h of 3 bar steam through Kv 100");

            var p2 = valve.OutletPressure.GetValueOrDefault();

            // 300 kg/h through Kv 100 is a long way short of choking, so the drop is small: the
            // outlet has to land just below the inlet, and above the sonic limit of P1/2.
            Assert.That(p2, Is.GreaterThan(InletPressure / 2.0),
                        "the outlet pressure came out at or below the choked limit");
            Assert.That(p2, Is.LessThanOrEqualTo(InletPressure),
                        "the valve raised the pressure");
            Assert.That(p2, Is.GreaterThan(InletPressure * 0.9),
                        $"a duty this far from choking should barely drop the pressure, got {p2} Pa");
            Assert.That(valve.DeltaP.GetValueOrDefault(), Is.EqualTo(InletPressure - p2).Within(1.0));
        }

        /// <summary>
        /// The choked-flow limit the gas branch believes in. With the normal density 170x low it
        /// came out near 270 kg/h, just under the 300 kg/h the flowsheet asked for, which is the
        /// whole reason the quadratic had no root. The real limit is over 3000 kg/h.
        /// </summary>
        [Test]
        public void TheGasBranchChokedLimitIsPhysical()
        {
            var (_, valve, inlet, _) = Build(Valve.CalculationMode.Kv_Gas);

            valve.RunDynamicModel();

            var Ti = inlet.GetTemperature();
            var normal = 18.01528 / 22.414;
            var chokedKgPerHour = 259.5 * Kv * (InletPressure / 100000.0) / Math.Sqrt(Ti / normal);

            Assert.That(chokedKgPerHour, Is.GreaterThan(3000.0),
                        "3 bar steam through Kv 100 chokes in the thousands of kg/h, not the hundreds");
            Assert.That(MassFlow * 3600.0, Is.LessThan(chokedKgPerHour / 5.0),
                        "the test case has to sit well clear of the limit for this to prove anything");
        }

        /// <summary>
        /// Ten steps at a fixed opening: the reported failure did not appear on the first step but
        /// once the run was under way and the streams carried computed rather than entered values.
        /// </summary>
        [TestCase(Valve.CalculationMode.Kv_General)]
        [TestCase(Valve.CalculationMode.Kv_Steam)]
        public void RepeatedStepsStayInRange(Valve.CalculationMode mode)
        {
            var (_, valve, _, outlet) = Build(mode);

            for (var step = 0; step < 10; step++)
            {
                var i = step;
                Assert.That(() => valve.RunDynamicModel(), Throws.Nothing,
                            $"{mode} threw on step {i}");
                Assert.That(valve.OutletPressure.GetValueOrDefault(),
                            Is.GreaterThan(InletPressure / 2.0).And.LessThanOrEqualTo(InletPressure),
                            $"{mode} left the outlet pressure out of range on step {i}");

                outlet.SetMassFlow(MassFlow);
            }
        }

        /// <summary>
        /// Closing the valve down raises the drop, and the model has to stay solvable until the flow
        /// really is beyond what the opening can pass. Below that it has to say so in terms of the
        /// limit it hit, since "unable to calculate the outlet pressure" on its own named neither
        /// the flow nor the Kv and sent the reporter after their controller tuning instead.
        /// </summary>
        [Test]
        public void ClosingTheValveEitherSolvesOrNamesTheChokedLimit()
        {
            foreach (var opening in new[] { 100.0, 50.0, 20.0, 10.0, 5.0, 2.0, 1.0 })
            {
                var (_, valve, _, _) = Build(Valve.CalculationMode.Kv_General);
                valve.OpeningPct = opening;

                try
                {
                    valve.RunDynamicModel();

                    Assert.That(valve.OutletPressure.GetValueOrDefault(),
                                Is.GreaterThan(InletPressure / 2.0).And.LessThanOrEqualTo(InletPressure),
                                $"outlet pressure out of range at {opening}% open");
                }
                catch (Exception ex)
                {
                    Assert.That(ex.Message, Does.Contain("choked"),
                                $"at {opening}% open the model failed without naming the limit: {ex.Message}");
                    Assert.That(ex.Message, Does.Contain("300"),
                                "the message should carry the flow that could not be passed");
                }
            }
        }
    }
}
