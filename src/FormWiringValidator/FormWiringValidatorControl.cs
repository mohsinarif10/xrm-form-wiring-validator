using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using FormWiringValidator.Checks;
using FormWiringValidator.Model;
using FormWiringValidator.Scope;
using XrmToolBox.Extensibility;

namespace FormWiringValidator
{
    public class FormWiringValidatorControl : PluginControlBase
    {
        private readonly CheckedListBox _prefixList = new CheckedListBox();
        private readonly DataGridView _results = new DataGridView();
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();
        private readonly ToolStripButton _scanButton = new ToolStripButton("Scan");
        private readonly ToolStripButton _exportButton = new ToolStripButton("Export CSV");

        private List<Finding> _findings = new List<Finding>();

        public FormWiringValidatorControl()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Dock = DockStyle.Fill;

            var toolbar = new ToolStrip();
            var loadButton = new ToolStripButton("Load prefixes");
            loadButton.Click += (s, e) => ExecuteMethod(LoadPrefixes);
            _scanButton.Click += (s, e) => ExecuteMethod(Scan);
            _scanButton.Enabled = false;
            _exportButton.Click += (s, e) => Export();
            _exportButton.Enabled = false;
            toolbar.Items.Add(loadButton);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(_scanButton);
            toolbar.Items.Add(_exportButton);

            var statusBar = new StatusStrip();
            _status.Text = "Connect to an environment, then load the publisher prefixes.";
            statusBar.Items.Add(_status);

            var left = new Panel { Dock = DockStyle.Left, Width = 230, Padding = new Padding(8) };
            var caption = new Label
            {
                Dock = DockStyle.Top,
                Height = 38,
                Text = "Scan these publisher prefixes:"
            };
            _prefixList.Dock = DockStyle.Fill;
            _prefixList.CheckOnClick = true;
            left.Controls.Add(_prefixList);
            left.Controls.Add(caption);

            _results.Dock = DockStyle.Fill;
            _results.ReadOnly = true;
            _results.AllowUserToAddRows = false;
            _results.AllowUserToDeleteRows = false;
            _results.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _results.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _results.RowHeadersVisible = false;

            Controls.Add(_results);
            Controls.Add(left);
            Controls.Add(toolbar);
            Controls.Add(statusBar);
        }

        private void LoadPrefixes()
        {
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Reading publishers...",
                Work = (worker, args) =>
                {
                    args.Result = PrefixDetector.Detect(Service);
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(args.Error.Message, "Could not read publishers",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var prefixes = (List<string>)args.Result;
                    _prefixList.Items.Clear();
                    foreach (var prefix in prefixes)
                    {
                        _prefixList.Items.Add(prefix, PrefixDetector.TickByDefault(prefix));
                    }

                    _scanButton.Enabled = prefixes.Count > 0;
                    _status.Text = prefixes.Count + " customer prefixes found. "
                        + "Microsoft's own prefixes are left out, because their scripts live inside "
                        + "the platform and checking them only produces noise.";
                }
            });
        }

        private void Scan()
        {
            var prefixes = _prefixList.CheckedItems.Cast<string>().ToList();
            if (prefixes.Count == 0)
            {
                MessageBox.Show("Tick at least one prefix to scan.", "Nothing selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var checker = new FormWiringChecker(Service);

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Scanning forms...",
                Work = (worker, args) =>
                {
                    args.Result = checker.Run(prefixes, message => worker.ReportProgress(0, message));
                },
                ProgressChanged = args =>
                {
                    SetWorkingMessage(args.UserState as string);
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(args.Error.Message, "Scan failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    _findings = (List<Finding>)args.Result;
                    ShowResults();
                }
            });
        }

        private void ShowResults()
        {
            _results.DataSource = _findings
                .OrderBy(f => f.Severity)
                .ThenBy(f => f.Table)
                .ThenBy(f => f.Form)
                .Select(f => new
                {
                    f.Severity,
                    f.Table,
                    f.Form,
                    f.Event,
                    f.Handler,
                    f.Library,
                    f.Problem,
                    f.Fix
                })
                .ToList();

            foreach (DataGridViewRow row in _results.Rows)
            {
                if (Equals(row.Cells[0].Value, Severity.Error))
                {
                    row.DefaultCellStyle.ForeColor = Color.Firebrick;
                }
            }

            _exportButton.Enabled = _findings.Count > 0;

            _status.Text = _findings.Count == 0
                ? "No problems found. Nothing is wired to a function that does not exist."
                : _findings.Count + " problems found.";
        }

        private void Export()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "CSV file|*.csv",
                FileName = "form-wiring-findings.csv"
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var csv = new StringBuilder();
                csv.AppendLine("Severity,Table,Form,Event,Handler,Library,Problem,Fix");
                foreach (var f in _findings)
                {
                    csv.AppendLine(string.Join(",", new[]
                    {
                        Quote(f.Severity.ToString()), Quote(f.Table), Quote(f.Form), Quote(f.Event),
                        Quote(f.Handler), Quote(f.Library), Quote(f.Problem), Quote(f.Fix)
                    }));
                }

                File.WriteAllText(dialog.FileName, csv.ToString(), Encoding.UTF8);
                _status.Text = "Exported " + _findings.Count + " findings to " + dialog.FileName;
            }
        }

        private static string Quote(string value)
        {
            var text = value ?? string.Empty;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
