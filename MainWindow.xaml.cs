// MainWindow.xaml.cs
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MtpSimulator.Models;

namespace MtpSimulator
{
    public partial class MainWindow : Window
    {
        private readonly List<OpcUaItem> _items;
        private readonly OpcUaServerHost _serverHost;

        // Bind this to the DataGridComboBoxColumn for DataType
        public List<string> SupportedDataTypes { get; } = new()
        {
            "Boolean", "Double", "Int16", "Int32", "Int64", "UInt32", "UInt64", "Byte", "DateTime", "String"
        };

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            _items = new List<OpcUaItem>();
            GridItems.ItemsSource = _items;

            _serverHost = new OpcUaServerHost();
            SetUiState(isRunning: false);
        }

        private void Log(string s)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
                TxtLog.ScrollToEnd();
            });
        }

        private void BtnLoadAml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "AML files (*.aml;*.xml)|*.aml;*.xml|All files|*.*"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                Log("Loading AML: " + dlg.FileName);
                var items = AmlLoader.LoadOpcUaItemsFromAml(dlg.FileName);

                _items.Clear();
                _items.AddRange(items);
                GridItems.Items.Refresh();

                Log($"Loaded {_items.Count} OPC UA items from AML.");
            }
            catch (Exception ex)
            {
                Log("Failed to load AML: " + ex.Message);
            }
        }

        private async void BtnStartServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Make sure any in-progress grid edits are committed
                CommitGridEdits();

                // Normalize/fill missing DataType/InitialValue so server has what it needs
                FillDefaults(_items);

                // Validate that InitialValue strings match the chosen DataType
                if (!TryValidateItems(_items, out var errorMsg))
                {
                    MessageBox.Show(this, errorMsg, "Invalid Initial Values", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Log("Validation failed. Fix the highlighted issues and try again.");
                    return;
                }

                Log("Starting server...");
                await _serverHost.StartAsync(_items);
                TxtEndpoint.Text = _serverHost.EndpointUrl;

                // IMPORTANT: do NOT auto-start simulation.
                // This allows UAExpert to see your InitialValue as-is first.
                Log($"Server started at {_serverHost.EndpointUrl}. Simulation is STOPPED. " +
                    "Verify initial values in UAExpert, then click 'Start Simulation'.");

                SetUiState(isRunning: true);
            }
            catch (Exception ex)
            {
                Log("Server start failed: " + ex.Message);
            }
        }

        private async void BtnStopServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("Stopping server...");

                // stop simulation first (best-effort)
                try { await _serverHost.StopSimulationAsync(); } catch { /* ignore */ }

                await _serverHost.StopAsync();
                TxtEndpoint.Text = string.Empty;

                Log("Server stopped.");
                SetUiState(isRunning: false);
            }
            catch (Exception ex)
            {
                Log("Server stop failed: " + ex.Message);
            }
        }

        private async void BtnStartSim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var interval = ParseIntervalOrDefault();
                await _serverHost.StartSimulationAsync(interval);
                Log($"Simulation started @ {interval} ms.");
            }
            catch (Exception ex)
            {
                Log("Failed to start simulation: " + ex.Message);
            }
        }

        private async void BtnStopSim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _serverHost.StopSimulationAsync();
                Log("Simulation stopped.");
            }
            catch (Exception ex)
            {
                Log("Failed to stop simulation: " + ex.Message);
            }
        }

        private void BtnFillDefaults_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdits();
            FillDefaults(_items);
            GridItems.Items.Refresh();
            Log("Filled defaults for missing DataType/InitialValue.");
        }

        private void CommitGridEdits()
        {
            // Ensure any active cell/row edits are committed before we read _items
            GridItems.CommitEdit(DataGridEditingUnit.Cell, true);
            GridItems.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private int ParseIntervalOrDefault()
        {
            const int fallback = 1000;
            if (int.TryParse(TxtSimInterval.Text, out var ms) && ms >= 100)
                return ms;
            return fallback;
        }

        /// <summary>
        /// Fill in DataType/InitialValue when the AML didn't provide them,
        /// or the user left them blank. Keeps your grid and server behavior aligned.
        /// </summary>
        private void FillDefaults(IEnumerable<OpcUaItem> items)
        {
            foreach (var it in items)
            {
                // Default DataType if missing
                if (string.IsNullOrWhiteSpace(it.DataType))
                    it.DataType = "String";

                // If InitialValue missing, use a sensible default for display & server
                if (string.IsNullOrWhiteSpace(it.InitialValue))
                {
                    it.InitialValue = it.DataType switch
                    {
                        "Boolean" => "false",
                        "Double" => "0",
                        "Int16" => "0",
                        "Int32" => "0",
                        "Int64" => "0",
                        "UInt32" => "0",
                        "UInt64" => "0",
                        "Byte" => "0",
                        "DateTime" => DateTime.UtcNow.ToString("o"),
                        _ => "" // String default is empty
                    };
                }
            }
        }

        /// <summary>
        /// Validate that each InitialValue can be parsed to the declared DataType.
        /// Mirrors the parsing logic in the server to fail fast in the UI.
        /// </summary>
        private bool TryValidateItems(IEnumerable<OpcUaItem> items, out string errorMessage)
        {
            int row = 0;
            foreach (var it in items)
            {
                row++;
                var dt = (it.DataType ?? "String").Trim();

                string s = it.InitialValue ?? string.Empty;

                bool ok = dt switch
                {
                    "Boolean" => bool.TryParse(s, out _),
                    "Double" => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _)
                                  || double.TryParse(s, out _), // fallback to current culture
                    "Int16" => short.TryParse(s, out _),
                    "Int32" => int.TryParse(s, out _),
                    "Int64" => long.TryParse(s, out _),
                    "UInt32" => uint.TryParse(s, out _),
                    "UInt64" => ulong.TryParse(s, out _),
                    "Byte" => byte.TryParse(s, out _),
                    "DateTime" => DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out _)
                                  || DateTime.TryParse(s, out _),
                    _ => true // String always OK
                };

                if (!ok)
                {
                    errorMessage = $"Row {row} ('{it.Name ?? it.Identifier ?? it.Id ?? "-"}'): " +
                                   $"InitialValue '{it.InitialValue}' is not a valid {dt}.";
                    return false;
                }
            }

            errorMessage = string.Empty;
            return true;
        }

        private void SetUiState(bool isRunning)
        {
            BtnStartServer.IsEnabled = !isRunning;
            BtnStopServer.IsEnabled = isRunning;

            // Simulation buttons enabled only when server is running
            BtnStartSim.IsEnabled = isRunning;
            BtnStopSim.IsEnabled = isRunning;
        }
    }
}
