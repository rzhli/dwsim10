//    The volume-spec flashes: what they hand the property package while they search, and what
//    they return for a holdup whose specified volume no pressure in the search window can match.
//
//    A dynamic heat exchanger sizes each of its cells as a fixed volume holding a fixed number of
//    moles, then asks for the pressure that reconciles the two - a volume-temperature flash. For
//    liquid water the volume residual is almost flat in pressure, so there is no gradient for the
//    optimiser to follow and it wanders the full width of its window. COBYLA's bounds are penalty
//    terms rather than hard limits, so it also evaluates points outside that window: the trial
//    pressure walked through zero, IAPWS-IF97 refused to give a density there, and the flash threw
//
//        The IAPWS-IF97 steam tables cannot give the density of water at 354.39 K and 0.0000 bar
//
//    from inside a heat exchanger that was only holding warm water.
//
//    Underneath that, the objective function had a units defect that made the residual meaningless
//    for any liquid: it passed a phase's scalar mole fraction where AUX_LIQDENS takes a composition
//    vector. Option Strict is off in this project, so that bound to the (T, P, Pvp, phaseid)
//    overload instead and the mole fraction was read as a pressure.

using System;
using NUnit.Framework;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class VolumeSpecFlashTests
    {
        [OneTimeSetUp]
        public void Setup()
        {
            DWSIM.GlobalSettings.Settings.AutomationMode = true;
            DWSIM.GlobalSettings.Settings.InspectorEnabled = false;
            DWSIM.GlobalSettings.Settings.CultureInfo = "en";

            FlowsheetBase.FlowsheetBase.AddPropPacks();
        }

        private static DWSIM.DynamicRunner.Flowsheet WaterFlowsheet()
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            fs.AddCompound("Water");
            return fs;
        }

        /// <summary>
        /// A cell of a dynamic heat exchanger: a fixed volume holding the moles that fit in it at
        /// the given state, which is how InitSideCells sizes the holdup. Returns the stream and the
        /// molar volume UpdateCellPressure would ask the flash for.
        /// </summary>
        private static (DWSIM.Thermodynamics.Streams.MaterialStream ms,
                        DWSIM.Thermodynamics.PropertyPackages.PropertyPackage pp,
                        double Vspec)
            Cell(double T, double P, double volumescale = 1.0, bool steamtables = true)
        {
            var fs = WaterFlowsheet();

            DWSIM.Thermodynamics.PropertyPackages.PropertyPackage pp =
                steamtables
                    ? new DWSIM.Thermodynamics.PropertyPackages.SteamTablesPropertyPackage { Flowsheet = fs }
                    : new DWSIM.Thermodynamics.PropertyPackages.PengRobinsonPropertyPackage { Flowsheet = fs };

            var obj = fs.AddObject(
                DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.MaterialStream, 0, 0, "cell");

            var ms = (DWSIM.Thermodynamics.Streams.MaterialStream)fs.SimulationObjects[obj.Name];
            ms.SetFlowsheet(fs);
            ms.PropertyPackage = pp;
            ms.AssignSelfToPP();

            const double cellvolume = 0.1;   // m3, one of ten cells of a 1 m3 side

            ms.SetTemperature(T);
            ms.SetPressure(P);
            ms.SetMassFlow(1.0);
            ms.SetFlashSpec("PT");
            ms.Calculate();

            // Fill the cell: density x volume is the mass it holds.
            ms.SetMassFlow(ms.Phases[0].Properties.density.GetValueOrDefault() * cellvolume);
            ms.Calculate();

            var Vspec = cellvolume / ms.GetMolarFlow() * volumescale;

            // MaterialStream.Calculate() releases the package's current stream on the way out, and
            // CalculateEquilibrium2 reads the mixture composition off it.
            pp.CurrentMaterialStream = ms;

            return (ms, pp, Vspec);
        }

        /// <summary>
        /// The states a dynamic heat exchanger on the steam tables passes through, each asking for
        /// the volume its own holdup occupies. Every one of these threw before: subcooled water is
        /// the case that reached zero pressure, and the reported failure was a cell at 81.2 C.
        /// </summary>
        [TestCase(354.39, 101325.0, TestName = "TheVolumeTemperatureFlashHoldsSubcooledWater(81 C, at the exchanger minimum pressure)")]
        [TestCase(354.39, 150000.0, TestName = "TheVolumeTemperatureFlashHoldsSubcooledWater(81 C, at a valve outlet pressure)")]
        [TestCase(303.15, 101325.0, TestName = "TheVolumeTemperatureFlashHoldsSubcooledWater(30 C cold inlet)")]
        [TestCase(333.15, 101325.0, TestName = "TheVolumeTemperatureFlashHoldsSubcooledWater(60 C cold outlet)")]
        [TestCase(406.68, 300000.0, TestName = "TheVolumeTemperatureFlashHoldsSubcooledWater(133 C saturated steam at 3 bar)")]
        public void TheVolumeTemperatureFlashHoldsItsOwnState(double T, double Pref)
        {
            var (_, pp, Vspec) = Cell(T, Pref);

            IFlashCalculationResult result = null;

            Assert.That(() => result = pp.CalculateEquilibrium2(
                            FlashCalculationType.VolumeTemperature, Vspec, T, Pref),
                        Throws.Nothing,
                        $"the V-T flash threw for a cell at {T} K and {Pref} Pa");

            var P = result.CalculatedPressure.GetValueOrDefault();

            // The cell holds exactly what it holds at Pref, so that is the answer.
            Assert.That(P, Is.EqualTo(Pref).Within(1.0).Percent,
                        "the V-T flash did not recover the pressure the holdup was built at");
        }

        /// <summary>
        /// The same for Peng-Robinson, which extrapolates below its range instead of refusing. It
        /// never threw, so it never showed the units defect in the objective function - it just
        /// returned a pressure computed from a meaningless residual.
        /// </summary>
        [TestCase(354.39, 101325.0)]
        [TestCase(303.15, 101325.0)]
        public void TheVolumeTemperatureFlashHoldsItsOwnStateOnAnEquationOfState(double T, double Pref)
        {
            var (_, pp, Vspec) = Cell(T, Pref, steamtables: false);

            var result = pp.CalculateEquilibrium2(
                FlashCalculationType.VolumeTemperature, Vspec, T, Pref);

            Assert.That(result.CalculatedPressure.GetValueOrDefault(),
                        Is.EqualTo(Pref).Within(1.0).Percent,
                        "the V-T flash did not recover the pressure the holdup was built at");
        }

        /// <summary>
        /// A holdup whose specified volume no pressure in the search window can produce. Water is
        /// nearly incompressible, so asking a liquid-filled cell for a hundred times its own volume
        /// has no solution at all: the flash has to come back at the edge of its window with a
        /// physical pressure rather than throw, because the caller - UpdateCellPressure - clamps the
        /// result to the exchanger's minimum pressure and carries on.
        /// </summary>
        [TestCase(100.0, TestName = "TheVolumeTemperatureFlashStaysInBounds(100x the liquid volume)")]
        [TestCase(0.01, TestName = "TheVolumeTemperatureFlashStaysInBounds(1/100 of the liquid volume)")]
        public void TheVolumeTemperatureFlashStaysInBoundsWhenTheVolumeIsUnreachable(double volumescale)
        {
            const double T = 354.39;
            const double Pref = 101325.0;

            var (_, pp, Vspec) = Cell(T, Pref, volumescale);

            IFlashCalculationResult result = null;

            Assert.That(() => result = pp.CalculateEquilibrium2(
                            FlashCalculationType.VolumeTemperature, Vspec, T, Pref),
                        Throws.Nothing,
                        "the V-T flash threw on an unreachable volume instead of returning its best point");

            var P = result.CalculatedPressure.GetValueOrDefault();

            Assert.That(P, Is.GreaterThan(0.0), "the V-T flash returned a non-physical pressure");

            // Flash_VT searches [0.7, 1.4] x Pref. Landing on an edge is the right answer here;
            // landing outside it means a trial point escaped and was evaluated anyway.
            Assert.That(P, Is.InRange(Pref * 0.7 - 1.0, Pref * 1.4 + 1.0),
                        "the V-T flash returned a pressure from outside its own search window");
        }

        /// <summary>
        /// Two-phase water, where the volume really does depend on pressure: the vapour fraction
        /// sets the volume, so there is a gradient to follow and the flash has a genuine root.
        /// </summary>
        [Test]
        public void TheVolumeTemperatureFlashHoldsBoilingWater()
        {
            const double T = 373.15;
            const double Pref = 101325.0;

            // Half the volume its own vapour occupies, so the cell is part liquid, part vapour.
            var (_, pp, Vspec) = Cell(T, Pref, 0.5);

            IFlashCalculationResult result = null;

            Assert.That(() => result = pp.CalculateEquilibrium2(
                            FlashCalculationType.VolumeTemperature, Vspec, T, Pref),
                        Throws.Nothing, "the V-T flash threw on boiling water");

            var P = result.CalculatedPressure.GetValueOrDefault();

            Assert.That(P, Is.GreaterThan(0.0), "the V-T flash returned a non-physical pressure");
            Assert.That(P, Is.InRange(Pref * 0.7 - 1.0, Pref * 1.4 + 1.0),
                        "the V-T flash returned a pressure from outside its own search window");
        }

        /// <summary>
        /// The volume-pressure flash, the same search over temperature instead of pressure. It has
        /// the same objective function and the same defect; nothing in the shipped unit operations
        /// calls it, so it had no coverage at all.
        /// </summary>
        [TestCase(354.39)]
        [TestCase(303.15)]
        public void TheVolumePressureFlashHoldsItsOwnState(double T)
        {
            const double Pref = 101325.0;

            var (_, pp, Vspec) = Cell(T, Pref);

            IFlashCalculationResult result = null;

            Assert.That(() => result = pp.CalculateEquilibrium2(
                            FlashCalculationType.VolumePressure, Vspec, Pref, T),
                        Throws.Nothing,
                        $"the V-P flash threw for a cell at {T} K");

            var Tcalc = result.CalculatedTemperature.GetValueOrDefault();

            Assert.That(Tcalc, Is.GreaterThan(0.0), "the V-P flash returned a non-physical temperature");
            Assert.That(Tcalc, Is.InRange(T * 0.9 - 0.001, T * 1.1 + 0.001),
                        "the V-P flash returned a temperature from outside its own search window");
        }
    }
}
