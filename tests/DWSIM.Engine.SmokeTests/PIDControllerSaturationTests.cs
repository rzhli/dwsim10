using System;
using DWSIM.Thermodynamics.Streams;
using DWSIM.UnitOperations.SpecialOps;
using DWSIM.UnitOperations.UnitOperations;
using NUnit.Framework;
using ObjectType = DWSIM.Interfaces.Enums.GraphicObjects.ObjectType;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class PIDControllerSaturationTests
    {
        [Test, Combinatorial]
        public void SaturationDoesNotWindUpTheIntegralAndTheValveRecovers(
            [Values(false, true)] bool reverseActing, [Values(30.0, 90.0)] double initialTemperature,
            [Values(0, 1)] int form, [Values(0.0, 100.0)] double span)
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            var pv = (MaterialStream)fs.AddObject(ObjectType.MaterialStream, 0, 0, "PV");
            var valve = (Valve)fs.AddObject(ObjectType.Valve, 0, 0, "MV");
            var pid = (PIDController)fs.AddObject(ObjectType.Controller_PID, 0, 0, "PID");
            pid.SetFlowsheet(fs);
            pid.ControlledObjectData.ID = pv.Name;
            pid.ControlledObjectData.PropertyName = "PROP_MS_0";
            pid.ControlledObjectData.Units = "C";
            pid.ManipulatedObjectData.ID = valve.Name;
            pid.ManipulatedObjectData.PropertyName = "PROP_VA_5";
            pid.ManipulatedObjectData.Units = "";
            pid.SetPoint = 60.0;
            pid.Kp = 2.0;
            pid.Ki = 0.1;
            pid.Kd = 0.0;
            pid.ManipulatedVariableSpan = span;
            pid.PIDForm = form;
            pid.Offset = span > 0.0 ? 50.0 : 0.0;
            double neutralOpening = span > 0.0 ? 50.0 : 60.0;
            pid.OutputMin = 1.0;
            pid.OutputMax = 100.0;
            pid.WindupGuard = 20.0;
            pid.ReverseActing = reverseActing;

            var integrator = new DWSIM.DynamicsManager.Integrator { ID = "I", IntegrationStep = TimeSpan.FromSeconds(1.0) };
            fs.DynamicsManager.IntegratorList.Add(integrator.ID, integrator);
            fs.DynamicsManager.ScheduleList.Add("S", new DWSIM.DynamicsManager.Schedule { ID = "S", CurrentIntegrator = integrator.ID });
            fs.DynamicsManager.CurrentSchedule = "S";
            pv.SetTemperature(initialTemperature + 273.15);

            for (int i = 0; i < 100; i++) pid.Calculate();

            bool upperLimit = reverseActing == (initialTemperature > 60.0);
            Assert.That(valve.OpeningPct, Is.EqualTo(upperLimit ? 100.0 : 1.0).Within(1e-9));
            Assert.That(pid.ITerm, Is.EqualTo(0.0).Within(1e-9), "a saturated actuator must not accumulate more error in the same direction");

            pv.SetTemperature(333.15);
            pid.Calculate();

            Assert.That(valve.OpeningPct, Is.EqualTo(neutralOpening).Within(1e-8), "the accumulated saturation must not hold the valve at its limit after recovery");

            pv.SetTemperature(334.15);
            pid.Calculate();
            Assert.That(pid.ITerm, Is.GreaterThan(0.0), "integration must remain active away from a limit");
            Assert.That(valve.OpeningPct, reverseActing ? Is.GreaterThan(neutralOpening) : Is.LessThan(neutralOpening));
        }
    }
}
