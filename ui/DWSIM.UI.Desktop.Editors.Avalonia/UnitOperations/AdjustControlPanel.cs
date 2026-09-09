using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.UI.Shared.Avalonia;
using Adjust = DWSIM.UnitOperations.SpecialOps.Adjust;
using cv = DWSIM.SharedClasses.SystemsOfUnits.Converter;

namespace DWSIM.UI.Desktop.Editors
{

    /// <summary>
    /// The control panel of an Adjust block: it drives the manipulated variable until the
    /// controlled one reaches the set point, solving the flowsheet at every step, which is what
    /// the Windows control panel does when the block is not left to the flowsheet solver.
    /// </summary>
    public static class AdjustControlPanel
    {

        /// <summary>The convergence methods, in the order the Windows combo lists them.</summary>
        private static readonly List<string> Methods = new List<string>
        {
            "Secant", "Brent", "Newton", "IPOPT"
        };

        private sealed class IterationRow
        {
            public string Iteration { get; set; } = "";
            public string Manipulated { get; set; } = "";
            public string Controlled { get; set; } = "";
            public string SetPoint { get; set; } = "";
            public string Error { get; set; } = "";
        }

        public static void Show(Adjust adjust)
        {
            var flowsheet = adjust.GetFlowsheet();
            var su = flowsheet.FlowsheetOptions.SelectedUnitSystem;
            var nf = flowsheet.FlowsheetOptions.NumberFormat;

            var panel = AvaloniaCommon.GetDefaultContainer();
            var window = AvaloniaCommon.GetDefaultEditorForm(
                adjust.GraphicObject.Tag + ": Control Panel", 640, 660, panel);

            if (adjust.ManipulatedObject == null || adjust.ControlledObject == null)
            {
                panel.CreateAndAddDescriptionRow(
                    "Pick the manipulated and the controlled variables before running the adjust.");
                window.Show();
                return;
            }

            var manipulatedUnit = adjust.ManipulatedObject.GetPropertyUnit(
                adjust.ManipulatedObjectData.PropertyName, su);
            var controlledUnit = adjust.ControlledObject.GetPropertyUnit(
                adjust.ControlledObjectData.PropertyName, su);

            SeedLimits(adjust, su, manipulatedUnit);

            panel.CreateAndAddLabelRow("Parameters");

            var method = panel.CreateAndAddDropDownRow("Convergence method", Methods,
                Math.Max(0, adjust.SolvingMethodSelf), (dd, e) => adjust.SolvingMethodSelf = dd.SelectedIndex);

            panel.CreateAndAddTextBoxRow(nf, "Adjust value (" + controlledUnit + ")",
                AdjustEditor.SetPointInDisplayUnit(adjust),
                (tb, e) =>
                {
                    if (UnitOpEditorRows.TryParse(tb.Text, out var v))
                        adjust.AdjustValue = AdjustEditor.SetPointToSI(adjust, v);
                });

            panel.CreateAndAddTextBoxRow(nf, "Tolerance", adjust.Tolerance,
                (tb, e) => { if (UnitOpEditorRows.TryParse(tb.Text, out var v)) adjust.Tolerance = v; });

            panel.CreateAndAddTextBoxRow(nf, "Step size", adjust.StepSize,
                (tb, e) => { if (UnitOpEditorRows.TryParse(tb.Text, out var v)) adjust.StepSize = v; });

            panel.CreateAndAddTextBoxRow(nf, "Maximum iterations", adjust.MaximumIterations,
                (tb, e) => { if (UnitOpEditorRows.TryParse(tb.Text, out var v)) adjust.MaximumIterations = (int)v; });

            panel.CreateAndAddLabelRow("Min / Max Limits (" + manipulatedUnit + ")");

            panel.CreateAndAddTextBoxRow(nf, "Minimum", adjust.MinVal.GetValueOrDefault(),
                (tb, e) => { if (UnitOpEditorRows.TryParse(tb.Text, out var v)) adjust.MinVal = v; });

            panel.CreateAndAddTextBoxRow(nf, "Maximum", adjust.MaxVal.GetValueOrDefault(),
                (tb, e) => { if (UnitOpEditorRows.TryParse(tb.Text, out var v)) adjust.MaxVal = v; });

            panel.CreateAndAddLabelRow("Results");

            var status = panel.CreateAndAddTwoLabelsRow("Status", "Idle");
            var iteration = panel.CreateAndAddTwoLabelsRow("Iteration", "");
            var error = panel.CreateAndAddTwoLabelsRow("Current error", "");

            var rows = new ObservableCollection<IterationRow>();

            var grid = new DataGrid
            {
                ItemsSource = rows,
                AutoGenerateColumns = false,
                CanUserSortColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                Height = 220
            };

            grid.Columns.Add(Column("Iteration", "Iteration", 0.8));
            grid.Columns.Add(Column("MV", "Manipulated", 1.2));
            grid.Columns.Add(Column("CV", "Controlled", 1.2));
            grid.Columns.Add(Column("SP", "SetPoint", 1.2));
            grid.Columns.Add(Column("Error", "Error", 1.2));

            panel.Children.Add(grid);

            Button start = null, stop = null;
            var cancel = false;

            start = panel.CreateAndAddButtonRow("Start Adjust", null, (btn, e) =>
            {
                cancel = false;
                rows.Clear();
                start.IsEnabled = false;
                if (stop != null) stop.IsEnabled = true;
                status.Text = "Adjusting";

                Run(adjust, nf,
                    () => cancel, rows, status, iteration, error,
                    () =>
                    {
                        start.IsEnabled = true;
                        if (stop != null) stop.IsEnabled = false;
                    });
            });

            stop = panel.CreateAndAddButtonRow("Stop", null, (btn, e) => cancel = true);
            stop.IsEnabled = false;

            window.Show();
        }

        /// <summary>
        /// The limits the Windows panel fills in the first time it is opened: a fifth and twice
        /// the current value of the manipulated variable.
        /// </summary>
        private static void SeedLimits(Adjust adjust, IUnitsOfMeasure su, string manipulatedUnit)
        {
            if (adjust.MinVal.HasValue && adjust.MaxVal.HasValue) return;

            try
            {
                var current = Convert.ToDouble(adjust.ManipulatedObject.GetPropertyValue(
                    adjust.ManipulatedObjectData.PropertyName));

                if (!adjust.MinVal.HasValue)
                    adjust.MinVal = cv.ConvertFromSI(manipulatedUnit, current * 0.2);
                if (!adjust.MaxVal.HasValue)
                    adjust.MaxVal = cv.ConvertFromSI(manipulatedUnit, current * 2.0);
            }
            catch (Exception)
            {
            }
        }

        private static void Run(Adjust adjust, string nf,
                                Func<bool> cancelled,
                                ObservableCollection<IterationRow> rows,
                                TextBlock status, TextBlock iterationLabel, TextBlock errorLabel,
                                Action finished)
        {
            var flowsheet = adjust.GetFlowsheet();
            var start = Convert.ToDouble(adjust.ManipulatedObject.GetPropertyValue(
                adjust.ManipulatedObjectData.PropertyName));

            Task.Factory.StartNew(() => Solve(adjust, cancelled, (index, manipulated, controlled, setPoint) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    iterationLabel.Text = (index + 1) + " of " + adjust.MaximumIterations;
                    errorLabel.Text = (controlled - setPoint).ToString("G6", CultureInfo.CurrentCulture);
                    rows.Add(new IterationRow
                    {
                        Iteration = index.ToString(),
                        Manipulated = manipulated.ToString(nf, CultureInfo.CurrentCulture),
                        Controlled = controlled.ToString(nf, CultureInfo.CurrentCulture),
                        SetPoint = setPoint.ToString(nf, CultureInfo.CurrentCulture),
                        Error = (controlled - setPoint).ToString(nf, CultureInfo.CurrentCulture)
                    });
                });
            }))
            .ContinueWith(task =>
            {
                var failed = task.Exception != null;

                // A failed run is rewound to the value the manipulated variable started from.
                if (failed)
                {
                    try
                    {
                        adjust.ManipulatedObject.SetPropertyValue(
                            adjust.ManipulatedObjectData.PropertyName, start);
                        DWSIM.FlowsheetSolver.FlowsheetSolver.SolveFlowsheet(flowsheet,
                            GlobalSettings.Settings.SolverMode);
                    }
                    catch (Exception)
                    {
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    adjust.GraphicObject.Calculated = !failed;
                    status.Text = failed
                        ? "Failed: " + task.Exception.InnerException.Message
                        : "Value adjusted successfully.";
                    flowsheet.UpdateInterface();
                    flowsheet.UpdateOpenEditForms();
                    finished();
                });
            });
        }

        /// <summary>
        /// Solves and verifies an adjustment. Residuals and tolerance use the controlled property's
        /// display unit: a 0.1 C error must not be converted as an absolute temperature of 273.25 K.
        /// The returned solution is applied and recalculated before the control panel reports success.
        /// </summary>
        internal static double Solve(Adjust adjust, Func<bool> cancelled = null,
                                     Action<int, double, double, double> reportIteration = null)
        {
            var flowsheet = adjust.GetFlowsheet();
            var su = flowsheet.FlowsheetOptions.SelectedUnitSystem;
            var manipulatedUnit = adjust.ManipulatedObject.GetPropertyUnit(
                adjust.ManipulatedObjectData.PropertyName, su);
            var controlledUnit = adjust.ControlledObject.GetPropertyUnit(
                adjust.ControlledObjectData.PropertyName, su);
            var tolerance = adjust.Tolerance;
            var minimum = cv.ConvertToSI(manipulatedUnit, adjust.MinVal.GetValueOrDefault());
            var maximum = cv.ConvertToSI(manipulatedUnit, adjust.MaxVal.GetValueOrDefault());
            var start = Convert.ToDouble(adjust.ManipulatedObject.GetPropertyValue(
                adjust.ManipulatedObjectData.PropertyName));
            var maxIterations = adjust.MaximumIterations;
            if (!double.IsFinite(tolerance) || tolerance <= 0.0 || maxIterations <= 0)
                throw new ArgumentException("Tolerance and maximum iterations must be positive.");
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum >= maximum)
                throw new ArgumentException("The minimum limit must be smaller than the maximum limit.");
            if (!double.IsFinite(start))
                throw new ArgumentException("The manipulated variable must have a finite initial value.");
            start = Math.Clamp(start, minimum, maximum);
            var count = 0;

            double SetPoint()
            {
                if (!adjust.Referenced) return cv.ConvertFromSI(controlledUnit, adjust.AdjustValue);

                var reference = Convert.ToDouble(flowsheet.SimulationObjects[adjust.ReferencedObjectData.ID]
                    .GetPropertyValue(adjust.ReferencedObjectData.PropertyName, su));
                var unit = flowsheet.SimulationObjects[adjust.ReferencedObjectData.ID]
                    .GetPropertyUnit(adjust.ReferencedObjectData.PropertyName, su);
                var offset = su.GetUnitType(unit) == UnitOfMeasure.temperature
                    ? cv.ConvertFromSI(unit + ".", adjust.AdjustValue)
                    : cv.ConvertFromSI(unit, adjust.AdjustValue);
                return reference + offset;
            }

            double Residual(double x)
            {
                if (cancelled?.Invoke() == true)
                    throw new TaskCanceledException("Adjust cancelled by the user.");
                if (!double.IsFinite(x))
                    throw new ArithmeticException("The adjusted value is not finite.");
                adjust.ManipulatedObject.SetPropertyValue(adjust.ManipulatedObjectData.PropertyName, x);

                var errors = DWSIM.FlowsheetSolver.FlowsheetSolver.SolveFlowsheet(flowsheet,
                    GlobalSettings.Settings.SolverMode);
                if (errors != null && errors.Count > 0)
                    throw new AggregateException("Flowsheet calculation failed during adjustment.", errors);

                var controlled = Convert.ToDouble(adjust.ControlledObject.GetPropertyValue(
                    adjust.ControlledObjectData.PropertyName, su));
                var setPoint = SetPoint();
                var error = controlled - setPoint;
                if (!double.IsFinite(error))
                    throw new ArithmeticException("The controlled value or set-point is not finite.");
                reportIteration?.Invoke(count++, cv.ConvertFromSI(manipulatedUnit, x), controlled, setPoint);
                return error;
            }

            if (Math.Abs(Residual(start)) <= tolerance) return start;

            // Root finders stop on the manipulated variable's interval, which is unrelated to
            // the allowed error of the controlled variable (e.g. kg/s versus degrees Celsius).
            var rootAccuracy = Math.Max(1.0, Math.Abs(start)) * 1.0e-10;
            double RootResidual(double x)
            {
                var error = Residual(x);
                return Math.Abs(error) <= tolerance ? 0.0 : error;
            }
            double solution;
            switch (adjust.SolvingMethodSelf)
            {
                case 1:
                    solution = MathNet.Numerics.RootFinding.Brent.FindRoot(RootResidual,
                        minimum, maximum, rootAccuracy, maxIterations);
                    break;
                case 2:
                    var newton = new DWSIM.MathOps.MathEx.Optimization.NewtonSolver
                    {
                        EnableDamping = false,
                        MaxIterations = maxIterations,
                        Tolerance = tolerance * tolerance
                    };
                    solution = newton.Solve(x => new[] { Residual(x[0]) }, new[] { start })[0];
                    break;
                case 3:
                    var ipopt = new DWSIM.MathOps.MathEx.Optimization.IPOPTSolver
                    {
                        MaxIterations = maxIterations,
                        Tolerance = tolerance * tolerance
                    };
                    solution = ipopt.Solve(x => Math.Pow(Residual(x[0]), 2.0), null,
                        new[] { start }, new[] { minimum }, new[] { maximum })[0];
                    break;
                default:
                    var step = (maximum - minimum) * 0.01;
                    // MathNet's secant method requires both guesses strictly inside the bounds.
                    var guess = Math.Clamp(start, minimum + step, maximum - step);
                    var second = guess + step < maximum ? guess + step : guess - step;
                    solution = MathNet.Numerics.RootFinding.Secant.FindRoot(RootResidual,
                        guess, second, minimum, maximum, rootAccuracy, maxIterations);
                    break;
            }

            if (!double.IsFinite(solution) || solution < minimum || solution > maximum)
                throw new ArithmeticException("The adjusted value is outside the specified limits.");

            // IPOPT can stop at a flat minimum with a nonzero error. Also, the final function
            // evaluation can be a derivative probe rather than the solution returned by a solver.
            var finalError = Residual(solution);
            if (Math.Abs(finalError) > tolerance)
                throw new InvalidOperationException(
                    $"Target not reached: error {finalError:G6} {controlledUnit} exceeds tolerance {tolerance:G6} {controlledUnit}. " +
                    "Check the manipulated variable, limits and iteration count.");
            return solution;
        }

        private static DataGridTextColumn Column(string header, string path, double width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path) { Mode = BindingMode.OneWay },
                Width = new DataGridLength(width, DataGridLengthUnitType.Star)
            };
        }

    }

}
