﻿using LiveSplit.Model;
using LiveSplit.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.UI.Components
{
    public partial class ComponentSettings : UserControl, INotifyPropertyChanged
    {
        private class AutosplitSelection
        {
            internal AutosplitSelection(string autosplitName)
            {
                AutosplitName = autosplitName;
            }

            public string AutosplitName { get; set; }
        }

        private const string GamesElementName = "Games";
        private static readonly IEnumerable<string> _emptySplitChoices = new List<string> { string.Empty };

        private readonly LiveSplitState _state;
        private string _currentGameName = null;
        private string _currentCategoryName = null;
        private Binding _configBinding = null;
        private string _previousConfigJson = null;
        private IEnumerable<string> _splitChoices = _emptySplitChoices;
        private List<string> _cachedSplits = null;
        private bool _settingsChanged = false;
        private Dictionary<string, GameSettings> _gameSettingsMap = new Dictionary<string, GameSettings>();
        private Dictionary<ISegment, AutosplitSelection> _segmentMap = new Dictionary<ISegment, AutosplitSelection>();
        private string _device = string.Empty;
        private bool _resetSNES = false;
        private bool _showStatusMessage = true;

        public event PropertyChangedEventHandler PropertyChanged;

        public string Device
        {
            get => _device;
            set => SetAndNotifyIfChanged(ref _device, value);
        }

        public bool ResetSNES
        {
            get => _resetSNES;
            set => SetAndNotifyIfChanged(ref _resetSNES, value);
        }

        public bool ShowStatusMessage
        {
            get => _showStatusMessage;
            set => SetAndNotifyIfChanged(ref _showStatusMessage, value);
        }

        internal Game Config { get; private set; }

        private GameSettings CurrentGameSettings
        {
            get
            {
                if (!_gameSettingsMap.TryGetValue(_currentGameName, out GameSettings gameSettings))
                {
                    gameSettings = new GameSettings(_currentGameName, string.Empty);
                    _gameSettingsMap[_currentGameName] = gameSettings;
                }

                return gameSettings;
            }
        }

        private CategorySettings CurrentCategorySettings
        {
            get
            {
                if (!CurrentGameSettings.CategoryMap.TryGetValue(_currentCategoryName, out CategorySettings categorySettings))
                {
                    categorySettings = new CategorySettings(_currentCategoryName);
                    CurrentGameSettings.CategoryMap[_currentCategoryName] = categorySettings;
                }

                return categorySettings;
            }
        }

        private IEnumerable<string> SplitChoices
        {
            get => _splitChoices;
            set
            {
                if (!_splitChoices.SequenceEqual(value))
                {
                    _splitChoices = value;
                    foreach (var comboBox in splitsPanel.Controls.OfType<ComboBox>())
                    {
                        string previousSelection = (string)comboBox.SelectedItem;
                        ((BindingSource)comboBox.DataSource).DataSource = SplitChoices;
                        if (SplitChoices.Contains(previousSelection))
                        {
                            comboBox.SelectedItem = previousSelection;
                        }
                        else
                        {
                            comboBox.SelectedItem = string.Empty;
                        }
                    }

                    RefreshSplitSelections();
                }
            }
        }

        public ComponentSettings(LiveSplitState state)
        {
            _state = state;
            _currentGameName = _state.Run.GameName;
            _currentCategoryName = _state.Run.CategoryName;
            _state.RunManuallyModified += OnRunModified;

            InitializeComponent();

            txtDevice.DataBindings.Add(nameof(TextBox.Text), this, nameof(Device), false, DataSourceUpdateMode.OnPropertyChanged);
            chkReset.DataBindings.Add(nameof(CheckBox.Checked), this, nameof(ResetSNES), false, DataSourceUpdateMode.OnPropertyChanged);
            chkStatus.DataBindings.Add(nameof(CheckBox.Checked), this, nameof(ShowStatusMessage), false, DataSourceUpdateMode.OnPropertyChanged);

            UpdateConfigBinding();
            PopulateSplitsPanel();

            errorIcon.Image = SystemIcons.Error.ToBitmap();
        }

        public void SetSettings(XmlNode node)
        {
            var settingsElement = (XmlElement)node;
            var versionElement = settingsElement["Version"];
            int version = versionElement?.InnerText == "1.2" ? 1 : SettingsHelper.ParseInt(versionElement);
            switch (version)
            {
                case 1:
                    LoadSettings_1(settingsElement);
                    break;

                case 2:
                    LoadSettings_2(settingsElement);
                    break;

                case 3:
                    LoadSettings_3(settingsElement);
                    break;

                default:
                    Log.Error($"Tried to load Usb2SnesSplitter settings with unknown version: {version}");
                    break;
            }

            UpdateConfigBinding();
            RefreshSplitSelections();
        }

        public XmlNode GetSettings(XmlDocument document)
        {
            var parent = document.CreateElement("Settings");
            CreateSettingsNode(document, parent);
            return parent;
        }

        public int GetSettingsHashCode()
        {
            return CreateSettingsNode(null, null);
        }

        internal List<string> GetSplits()
        {
            if (!ReadConfig())
            {
                return null;
            }

            if (_settingsChanged)
            {
                _settingsChanged = false;
                if (ValidateSettings())
                {
                    errorMessage.Text = null;
                    _cachedSplits = new List<string>();
                    foreach (var segment in _state.Run)
                    {
                        _cachedSplits.Add(_segmentMap[segment].AutosplitName);
                    }
                }
                else
                {
                    _cachedSplits = null;
                }
            }

            return _cachedSplits;
        }

        private static string GetLabelTextFromSegmentName(string segmentName)
        {
            return $"{segmentName}:";
        }

        private static string GetSegmentNameFromLabelText(string labelText)
        {
            return labelText.Substring(0, labelText.Length - 1);
        }

        private void OnRunModified(object sender, EventArgs e)
        {
            if (_state.Run.GameName != _currentGameName ||_state.Run.CategoryName != _currentCategoryName)
            {
                SaveCurrentRunToSettings();
                _segmentMap.Clear();

                _currentGameName = _state.Run.GameName;
                _currentCategoryName = _state.Run.CategoryName;

                UpdateConfigBinding();
            }

            PopulateSplitsPanel();
        }

        private bool ReadConfig()
        {
            string configJson;
            try
            {
                configJson = File.ReadAllText(CurrentGameSettings.ConfigFile);
            }
            catch (Exception e)
            {
                _previousConfigJson = string.Empty;
                errorMessage.Text = "Could not read config file.\n" + e.Message;
                return false;
            }

            if (configJson != _previousConfigJson)
            {
                _previousConfigJson = configJson;
                _settingsChanged = true;
                try
                {
                    Config = Game.FromJSON(configJson);
                    SplitChoices = _emptySplitChoices.Concat(Config.definitions.Select(split => split.name).OrderBy(name => name));
                }
                catch (Exception e)
                {
                    Config = null;
                    errorMessage.Text = "Could not parse config file.\n" + e.Message;
                    return false;
                }
            }

            return Config != null;
        }

        private void SaveCurrentRunToSettings()
        {
            var splitMap = CurrentCategorySettings.SplitMap;
            splitMap.Clear();
            foreach (var entry in _segmentMap)
            {
                splitMap[entry.Key.Name] = entry.Value.AutosplitName;
            }
        }

        private void SetAndNotifyIfChanged<T>(ref T backingField, T newValue, [CallerMemberName] string propertyName = null)
        {
            if (!EqualityComparer<T>.Default.Equals(backingField, newValue))
            {
                backingField = newValue;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private void PopulateSplitsPanel()
        {
            bool segmentsChanged = false;
            if (_state.Run.Count == splitsPanel.RowCount)
            {
                int row = 0;
                foreach (var segment in _state.Run)
                {
                    var label = (Label)splitsPanel.GetControlFromPosition(0, row);
                    if (label == null || GetSegmentNameFromLabelText(label.Text) != segment.Name)
                    {
                        segmentsChanged = true;
                        break;
                    }

                    ++row;
                }
            }
            else
            {
                segmentsChanged = true;
            }

            if (segmentsChanged)
            {
                _settingsChanged = true;
                splitsPanel.RowCount = 0;
                splitsPanel.Controls.Clear();
                splitsPanel.RowStyles.Clear();

                int rowIndex = 0;
                foreach (var segment in _state.Run)
                {
                    ++splitsPanel.RowCount;
                    splitsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    var segmentLabel = new Label
                    {
                        Anchor = AnchorStyles.Left,
                        AutoSize = true,
                        Margin = new Padding(3),
                        Text = GetLabelTextFromSegmentName(segment.Name),
                    };

                    // Add this segment to the map if it's not already there
                    if (!_segmentMap.TryGetValue(segment, out var splitSelection))
                    {
                        splitSelection = new AutosplitSelection(string.Empty);
                        _segmentMap[segment] = splitSelection;
                    }

                    if (splitSelection.AutosplitName != segment.Name && Config?.definitions.Any(split => split.name == segment.Name) == true)
                    {
                        splitSelection.AutosplitName = segment.Name;
                    }
                    else if (string.IsNullOrEmpty(splitSelection.AutosplitName))
                    {
                        // This segment doesn't have an autosplit specified, so let's see if we can find a good default
                        if (CurrentCategorySettings.SplitMap.TryGetValue(segment.Name, out string autosplitName))
                        {
                            // We found a match for this segment's name in our loaded layout settings
                            splitSelection.AutosplitName = autosplitName;
                        }
                        else
                        {
                            // Check if the config has a split definition or alias whose name matches this segment
                            splitSelection.AutosplitName = Config?.GetAutosplitNameFromSegmentName(segment.Name) ?? string.Empty;
                        }
                    }

                    var comboBox = new ComboBox
                    {
                        Anchor = AnchorStyles.Left | AnchorStyles.Right,
                        DataSource = new BindingSource { DataSource = SplitChoices },
                        DropDownStyle = ComboBoxStyle.DropDownList,
                    };

                    bool ShouldBeEnabled()
                    {
                        var selection = (string)comboBox.SelectedItem;
                        return string.IsNullOrEmpty(selection) || selection != segment.Name;
                    }

                    comboBox.Enabled = ShouldBeEnabled();
                    comboBox.DataBindings.Add(nameof(ComboBox.SelectedItem), splitSelection, nameof(AutosplitSelection.AutosplitName), false, DataSourceUpdateMode.OnPropertyChanged);
                    comboBox.MouseWheel += (s, e) => ((HandledMouseEventArgs)e).Handled = true;
                    comboBox.SelectedValueChanged += (s, e) =>
                    {
                        comboBox.DataBindings[0].WriteValue();
                        comboBox.Enabled = ShouldBeEnabled();
                        _settingsChanged = true;
                    };

                    splitsPanel.Controls.Add(segmentLabel, 0, rowIndex);
                    splitsPanel.Controls.Add(comboBox, 1, rowIndex);
                    ++rowIndex;
                }
            }

            RefreshSplitSelections();
        }

        private void RefreshSplitSelections()
        {
            for (int row = 0; row < splitsPanel.RowCount; ++row)
            {
                var comboBox = (ComboBox)splitsPanel.GetControlFromPosition(1, row);
                if (string.IsNullOrEmpty((string)comboBox.SelectedItem))
                {
                    // This segment doesn't have an autosplit specified, so let's see if we can find a good default
                    var label = (Label)splitsPanel.GetControlFromPosition(0, row);
                    string segmentName = GetSegmentNameFromLabelText(label.Text);
                    string newSelection;
                    if (CurrentCategorySettings.SplitMap.TryGetValue(segmentName, out string autosplitName) && !string.IsNullOrEmpty(autosplitName))
                    {
                        // We found a match for this segment's name in our loaded layout settings
                        newSelection = autosplitName;
                    }
                    else
                    {
                        // Check if the config has a split definition or alias whose name matches this segment
                        newSelection = Config?.GetAutosplitNameFromSegmentName(segmentName) ?? string.Empty;
                    }

                    comboBox.SelectedItem = newSelection;
                }
            }
        }

        private void UpdateConfigBinding()
        {
            if (_configBinding?.DataSource != CurrentGameSettings)
            {
                if (_configBinding != null)
                {
                    txtConfigFile.DataBindings.Remove(_configBinding);
                }

                _configBinding = new Binding(nameof(TextBox.Text), CurrentGameSettings, nameof(GameSettings.ConfigFile), false, DataSourceUpdateMode.OnPropertyChanged);
                txtConfigFile.DataBindings.Add(_configBinding);

                ReadConfig();
            }
        }

        private bool ValidateSettings()
        {
            if (!string.Equals(_state.Run.GameName, Config.name, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage.Text = $"Game name from splits [{_state.Run.GameName}] does not match game name from config file [{Config.name}]";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Device))
            {
                errorMessage.Text = "You must specify a Device name";
                return false;
            }

            foreach (var segment in _state.Run)
            {
                if (!_segmentMap.TryGetValue(segment, out var autosplitSelection) || !Config.definitions.Any(split => split.name == autosplitSelection.AutosplitName))
                {
                    errorMessage.Text = $"Invalid split selection for segment [{segment.Name}]";
                    return false;
                }
            }

            return true;
        }

        private int CreateSettingsNode(XmlDocument document, XmlElement parent)
        {
            return SettingsHelper.CreateSetting(document, parent, "Version", 3) ^
            SettingsHelper.CreateSetting(document, parent, nameof(Device), Device) ^
            SettingsHelper.CreateSetting(document, parent, nameof(ResetSNES), ResetSNES) ^
            SettingsHelper.CreateSetting(document, parent, nameof(ShowStatusMessage), ShowStatusMessage) ^
            CreateGamesSettingsNode(document, parent);
        }

        private int CreateGamesSettingsNode(XmlDocument document, XmlElement parent)
        {
            SaveCurrentRunToSettings();

            var gamesElement = document?.CreateElement(GamesElementName);
            parent?.AppendChild(gamesElement);

            int result = 0;
            int count = 1;
            foreach (var gameSettings in _gameSettingsMap.Values.Where(g => !g.IsEmpty))
            {
                result ^= count++ * gameSettings.ToXml(document, gamesElement);
            }

            return result;
        }

        private void LoadSettings_1(XmlElement element)
        {
            Device = SettingsHelper.ParseString(element[nameof(Device)]);
            ResetSNES = SettingsHelper.ParseBool(element[nameof(ResetSNES)]);
            ShowStatusMessage = false;

            // If loading an older version of the component that only stored a single config file, try to read the config to figure out which
            // game it was for. If the config fails to open or parse, just ignore it.
            string configFile = SettingsHelper.ParseString(element["ConfigFile"]);
            try
            {
                Game config = Game.FromJSON(File.ReadAllText(configFile));
                _gameSettingsMap[config.name] = new GameSettings(config.name, configFile);
            }
            catch (Exception)
            {
            }
        }

        private void LoadSettings_2(XmlElement element)
        {
            LoadSettings_1(element);
            ShowStatusMessage = SettingsHelper.ParseBool(element[nameof(ShowStatusMessage)]);
        }

        private void LoadSettings_3(XmlElement settingsElement)
        {
            LoadSettings_2(settingsElement);

            _gameSettingsMap.Clear();
            foreach (var gameElement in settingsElement[GamesElementName].ChildNodes.Cast<XmlElement>())
            {
                GameSettings gameSettings = GameSettings.FromXml(gameElement);
                _gameSettingsMap[gameSettings.Name] = gameSettings;
            }
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            var ofd = new OpenFileDialog();
            ofd.Filter = "JSON Files|*.json";
            if(ofd.ShowDialog() == DialogResult.OK)
            {
                txtConfigFile.Text = ofd.FileName;
            }
        }

        private async void btnDetect_Click(object sender, EventArgs e)
        {
            USB2SnesW.USB2SnesW usb = new USB2SnesW.USB2SnesW();
            await usb.Connect();
            
            if (usb.Connected())
            {
                List<String> devices;
                devices = await usb.GetDevices();
                if (devices.Count > 0)
                    txtDevice.Text = devices[0];
                return;
            }
            MessageBox.Show("Could not auto-detect usb2snes compatible device, make sure it's connected and QUsb2Snes is running");
        }

        private void errorMessage_TextChanged(object sender, EventArgs e)
        {
            errorPanel.Visible = !string.IsNullOrEmpty(errorMessage.Text);
        }

        private void txtDevice_TextChanged(object sender, EventArgs e)
        {
            _settingsChanged = true;
        }
    }
}
