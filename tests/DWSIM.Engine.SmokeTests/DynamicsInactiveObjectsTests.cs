using System;
using System.Collections.Generic;
using DWSIM.Automation.DynamicRunner;
using DWSIM.Interfaces;
using DWSIM.SharedClasses.UnitOperations;
using DWSIM.UnitOperations.SpecialOps;
using NUnit.Framework;
using ObjectType = DWSIM.Interfaces.Enums.GraphicObjects.ObjectType;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class DynamicsInactiveObjectsTests
    {
        [TestCase(ObjectType.Controller_PID, true)]
        [TestCase(ObjectType.Controller_Python, true)]
        [TestCase(ObjectType.Controller_MPC, true)]
        [TestCase(ObjectType.Controller_PID, false)]
        [TestCase(ObjectType.Controller_Python, false)]
        [TestCase(ObjectType.Controller_MPC, false)]
        public void ControllersRespectCanvasActivityOnEveryStep(ObjectType type, bool controllerEnabled)
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            fs.FlowsheetOptions.ForceObjectSolving = true;
            var controller = (BaseClass)fs.AddObject(type, 0, 0, "controller");
            switch (controller)
            {
                case PIDController pid: pid.Active = controllerEnabled; break;
                case PythonController python: python.Active = controllerEnabled; break;
                case MPCController mpc: mpc.Active = controllerEnabled; break;
            }

            int calculations = 0;
            controller.OverrideCalculationRoutine = true;
            controller.CalculationRoutineOverride = () => calculations++;

            var integrator = new DWSIM.DynamicsManager.Integrator
            {
                ID = "I", IntegrationStep = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromSeconds(2)
            };
            fs.DynamicsManager.IntegratorList.Add(integrator.ID, integrator);
            fs.DynamicsManager.ScheduleList.Add("S", new DWSIM.DynamicsManager.Schedule
            {
                ID = "S", CurrentIntegrator = integrator.ID, UseCurrentStateAsInitial = true
            });
            fs.DynamicsManager.CurrentSchedule = "S";

            // The integrator caches its controller list. Activity changes during a run must
            // still take effect, without changing the controller's own enable switch.
            var result = new IntegratorRunner(fs).Run(new IntegratorRunOptions
            {
                RestoreInitialState = false,
                EnableHistorian = false,
                Solver = new NoProcessEquations(),
                PreStep = step => controller.GraphicObject.Active = step.tstep == 1,
                MaxWallTime = TimeSpan.FromSeconds(15)
            });

            Assert.That(result.Exceptions, Is.Empty);
            Assert.That(result.Completed, Is.True);
            Assert.That(result.Steps, Is.EqualTo(3));
            Assert.That(calculations, Is.EqualTo(controllerEnabled ? 1 : 0),
                "an Inactive canvas object must not run even when its controller enable switch is on");
        }

        // These tests exercise controller dispatch; no process equipment is needed.
        private sealed class NoProcessEquations : IFlowsheetSolver
        {
            public List<Exception> SolveFlowsheet(IFlowsheet flowsheet) => new List<Exception>();
        }
    }
}
