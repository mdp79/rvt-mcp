using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal sealed class PlanAgentProgressWindow : DrawingReviewWindow
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly List<PlanAgentOutcome> _outcomes = new();
        private readonly TextBlock _stage = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 45 };
        private readonly Button _stop = new() { Content = "Stop analysis", MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Right };
        private bool _finished;

        public PlanAgentProgressWindow(IReadOnlyList<PlanAgentJob> jobs, IReadOnlyList<byte[]> references)
        {
            Title = "Drawing Review | Codex analysis";
            Width = 475;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            var content = new StackPanel { Margin = new Thickness(18) };
            content.Children.Add(_stage);
            content.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 7, Margin = new Thickness(0, 2, 0, 16) });
            content.Children.Add(_stop);
            Content = content;
            _stop.Click += (_, _) => CancelAnalysis();
            Closing += (_, args) =>
            {
                if (_finished) return;
                args.Cancel = true;
                CancelAnalysis();
            };
            Loaded += async (_, _) =>
            {
                try
                {
                    for (int i = 0; i < jobs.Count && !_cancel.IsCancellationRequested; i++)
                    {
                        var job = jobs[i];
                        string caption = $"PDF page {job.Page} · {job.Role} · {i + 1} of {jobs.Count}";
                        _stage.Text = caption + "\nPreparing image…";
                        var progress = new Progress<string>(step =>
                        {
                            if (!_cancel.IsCancellationRequested) _stage.Text = caption + "\n" + step;
                        });
                        try
                        {
                            var marks = await PlanAgentRunner.AnalyzeAsync(job.Exchange,
                                job.Role == DrawingRole.Plan ? references : Array.Empty<byte[]>(), progress, _cancel.Token);
                            _outcomes.Add(new PlanAgentOutcome(job.Page, marks, null));
                        }
                        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _outcomes.Add(new PlanAgentOutcome(job.Page, null, ex.Message));
                        }
                    }
                }
                finally
                {
                    WasCancelled = _cancel.IsCancellationRequested;
                    _finished = true;
                    _cancel.Dispose();
                    Close();
                }
            };
        }

        public IReadOnlyList<PlanAgentOutcome> Outcomes => _outcomes;
        public bool WasCancelled { get; private set; }

        private void CancelAnalysis()
        {
            _stop.IsEnabled = false;
            _stage.Text = "Stopping analysis. Imported pages will remain in the project.";
            _cancel.Cancel();
        }
    }
}
