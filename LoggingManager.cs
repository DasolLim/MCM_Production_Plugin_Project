using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using ModbusCommander;
using ModbusCommander.Plugins;
using ModbusCommander.ViewEncoders;
using System.Runtime.Serialization;
using System.Xml;
using Modbus;

namespace ModbusCommanderWattsOn2Plugin
{
   public partial class LoggingManager : DummyWritableModbusForm
   {
      public override event ViewControl.RegisterChangedEventHandler RegisterChanged;

      //private BackgroundWorker downloader = null;

      private List<ComboBox> addressComboBoxes;
      private List<NumericUpDown> lengthUpDowns;

      private bool suppressAutoWrite = false;
      private int loggingPaused = 0;

      private List<RegisterWrite> outstandingWrites = new List<RegisterWrite>();

      private uint entries = 0;
      private int entrySize = 0;
      private bool loaded = false;

      private List<RegisterBlockDefinition> loggingConfigurationRegisters = new List<RegisterBlockDefinition>()
        {
            new RegisterBlockDefinition(0x4000, 0x40, 0x03)
        };

      private List<RegisterBlockDefinition> logSizeRegisters = new List<RegisterBlockDefinition>()
        {
            new RegisterBlockDefinition(0x4000, 0x16, 0x03)
        };

      private List<RegisterBlockDefinition> logPageBeginningRegisters = new List<RegisterBlockDefinition>()
        {
            new RegisterBlockDefinition(0x4100, 125, 0x03)
        };

      private List<RegisterBlockDefinition> logPageEndingRegisters = new List<RegisterBlockDefinition>()
        {
            new RegisterBlockDefinition(0x417D, 125, 0x03)
        };

      long lastUpdateTime = 0;

      private enum State
      {
         Standby,
         Preparation,
         ChangingPage,
         ReadingPage
      };

      private State state = State.Standby;
      private bool pageBeginning = true;
      private ushort[] memoryFile = null;
      private int readPosition = 0;
      private int writePosition = 0;
      private int page = 0;
      private int entriesDownloaded = 0;
      private String saveFilename = "";
      private uint latestEntryTimestamp = 0;
      private uint previousTimestamp = 0;

      private ModbusCommanderApi api;

      private bool clockEdited = false;
      private UnixTimestampStringViewEncoder timestampEncoder = new UnixTimestampStringViewEncoder();

      public LoggingManager() :
          this(null)
      {

      }

      /*
         Create a custom control that inherit from NumericUpDown class and overrides the UpdateEditText 
         method to format the number accordingly

         public class NumericUpDownMethod : NumericUpDown
         {
            public NumericUpDownMethod()
            {
            }

            protected override void UpdateData()
            {
               this.Text = "Auto";
            }
         }
      */

      /*
         Red Error
         -> 0x0158 ~ 0x0162

         And then, when the data is written into the meter, it should send 1 for 16-bit registers and 2 for 32 bit registers 
         (or larger numbers of the user picks a group, like in that task with "Summary registers")
      */

      public LoggingManager(ModbusCommanderApi api)
      {
         this.api = api;
         InitializeComponent();

         addressComboBoxes = new List<ComboBox>(15)
            {
               /*loggedAddressComboBox0,*/
                loggedAddressComboBox1,  loggedAddressComboBox2,  loggedAddressComboBox3,
                loggedAddressComboBox4,  loggedAddressComboBox5,  loggedAddressComboBox6,  loggedAddressComboBox7,
                loggedAddressComboBox8,  loggedAddressComboBox9,  loggedAddressComboBox10, loggedAddressComboBox11,
                loggedAddressComboBox12, loggedAddressComboBox13, loggedAddressComboBox14, loggedAddressComboBox15,
            };

         lengthUpDowns = new List<NumericUpDown>(15)
            {
               /*loggedLengthUpDown0,*/
                loggedLengthUpDown1,  loggedLengthUpDown2,  loggedLengthUpDown3,
                loggedLengthUpDown4,  loggedLengthUpDown5,  loggedLengthUpDown6,  loggedLengthUpDown7,
                loggedLengthUpDown8,  loggedLengthUpDown9,  loggedLengthUpDown10, loggedLengthUpDown11,
                loggedLengthUpDown12, loggedLengthUpDown13, loggedLengthUpDown14, loggedLengthUpDown15,
            };

         suppressAutoWrite = true;
         //timeComboBox.SelectedIndex = 0;
         downloadTimeUnitComboBox.SelectedIndex = 0;
         downloadTimeTextBox.Value = 1;
         downloadTimeTextBox.Enabled = false;
         downloadTimeUnitComboBox.SelectedIndexChanged += DownloadTimeUnitComboBox_SelectedIndexChanged;

         formatComboBox.SelectedIndex = 0;
         loggingStateComboBox.SelectedIndex = 0;
         timeUnitComboBox.SelectedIndex = 0;
         suppressAutoWrite = false;

         //populating contents inside addressComboBoxes
         for (int i = 0; i < 15; ++i)
         {
            int index = i;
            addressComboBoxes[i].DataSource = CreateRegisterList(DisplayRegisterDatabase.RegisterDatabase);
            addressComboBoxes[i].TextChanged += (s, e) => address_Changed(s, e, index, true);
            addressComboBoxes[i].LostFocus += (s, e) => address_Changed(s, e, index, false);
         }

         timestampEncoder.Style = TimeStyle.Minimal;
         clockTextBox.LostFocus += new EventHandler(clockTextBox_LostFocus);
         clockTextBox.KeyUp += new KeyEventHandler(clockTextBox_KeyUp);
         syncButton.Click += new EventHandler(syncButton_Click);

         openButton.Click += new EventHandler(openButton_Click);
         saveButton.Click += new EventHandler(saveButton_Click);
         saveAsButton.Click += new EventHandler(saveAsButton_Click);

      }

      private void DownloadTimeUnitComboBox_SelectedIndexChanged(object sender, EventArgs e)
      {
         downloadTimeTextBox.Enabled = downloadTimeUnitComboBox.SelectedIndex != 0;
      }

      private String openFile = null;
      [DataContract(Namespace = "")]
      private class Configuration
      {
         [DataMember]
         public ushort[] Values { get; set; }
         [DataMember]
         public uint Frequency { get; set; }
         [DataMember]
         public int TimeUnit { get; set; }
         [DataMember]
         public bool EraseWhenDone { get; set; }
         [DataMember]
         public bool PauseWhileDownloading { get; set; }
         [DataMember]
         public int Time { get; set; }
         [DataMember]
         public int FileFormat { get; set; }
         [DataMember]
         public int RegisterFormat { get; set; }
         [DataMember]
         public decimal DownloadTime { get; set; }
         [DataMember]
         public int DownloadTimeUnits { get; set; }

         public static void Save(String filename, Configuration file)
         {
            using (XmlWriter writer = XmlWriter.Create(filename, new XmlWriterSettings { Indent = true }))
            {
               DataContractSerializer serializer = new DataContractSerializer(
                   typeof(Configuration),
                   null,
                   Int32.MaxValue, false, false, null,
                   new AllowAllContractResolver()
                );

               serializer.WriteObject(writer, file);
            }
         }

         public static Configuration Load(String filename)
         {
            using (var stream = new FileStream(filename, FileMode.Open))
            {
               return Load(stream);
            }
         }

         public static Configuration Load(Stream stream)
         {
            DataContractSerializer serializer = new DataContractSerializer(
                typeof(Configuration),
                null,
                Int32.MaxValue, false, false, null,
                new AllowAllContractResolver()
             );

            Configuration loaded = (Configuration)serializer.ReadObject(stream);
            if (loaded.Time > 0 && loaded.Time <= 12)
            {
               /*0 "Entire Log",
                 1 "1 Hour",
                 2 "2 Hours",
                 3 "6 Hours",
                 4 "12 Hours",
                 5 "1 Day",
                 6 "2 Days",
                 7 "1 Week",
                 8 "2 Weeks",
                 9 "1 Month",
                10 "2 Months",
                11 "6 Months",
                12 "1 Year" */

               switch (loaded.Time)
               {
                  case 1: // 1 hour
                     loaded.DownloadTime = 1;
                     loaded.DownloadTimeUnits = 1;
                     break;
                  case 2:
                     loaded.DownloadTime = 2;
                     loaded.DownloadTimeUnits = 1;
                     break;

                  case 3:
                     loaded.DownloadTime = 6;
                     loaded.DownloadTimeUnits = 1;
                     break;

                  case 4:
                     loaded.DownloadTime = 12;
                     loaded.DownloadTimeUnits = 1;
                     break;

                  case 5:
                     loaded.DownloadTime = 1;
                     loaded.DownloadTimeUnits = 2;
                     break;

                  case 6:
                     loaded.DownloadTime = 2;
                     loaded.DownloadTimeUnits = 2;
                     break;

                  case 7:
                     loaded.DownloadTime = 1;
                     loaded.DownloadTimeUnits = 3;
                     break;

                  case 8:
                     loaded.DownloadTime = 2;
                     loaded.DownloadTimeUnits = 3;
                     break;

                  case 9:
                     loaded.DownloadTime = 1;
                     loaded.DownloadTimeUnits = 4;
                     break;

                  case 10:
                     loaded.DownloadTime = 2;
                     loaded.DownloadTimeUnits = 4;
                     break;

                  case 11:
                     loaded.DownloadTime = 6;
                     loaded.DownloadTimeUnits = 4;
                     break;

                  case 12:
                     loaded.DownloadTime = 1;
                     loaded.DownloadTimeUnits = 5;
                     break;

                  default:
                     loaded.DownloadTime = 1;
                     loaded.DownloadTimeUnits = 0;
                     break;
               }
            }

            return loaded;
         }

         public class AllowAllContractResolver : DataContractResolver
         {
            public override bool TryResolveType(Type dataContractType, Type declaredType, DataContractResolver knownTypeResolver, out XmlDictionaryString typeName, out XmlDictionaryString typeNamespace)
            {
               if (!knownTypeResolver.TryResolveType(dataContractType, declaredType, null, out typeName, out typeNamespace))
               {
                  var dictionary = new XmlDictionary();
                  typeName = dictionary.Add(dataContractType.FullName);
                  typeNamespace = dictionary.Add(dataContractType.Assembly.FullName);
               }
               return true;
            }

            public override Type ResolveName(string typeName, string typeNamespace, Type declaredType, DataContractResolver knownTypeResolver)
            {
               return knownTypeResolver.ResolveName(typeName, typeNamespace, declaredType, null) ?? Type.GetType(typeName + ", " + typeNamespace);
            }

         }
      }

      Configuration GetConfiguration()
      {
         int currentIndex = 0;
         ushort[] values = new ushort[32];
         for (currentIndex = 0; currentIndex < 15; ++currentIndex)
         {
            ushort address = GetAddress(currentIndex);
            ushort length = (ushort)lengthUpDowns[currentIndex].Value;

            if (length > FindMaximumLength(address))
               throw new FormatException("Block is too large.");

            values[currentIndex * 2] = address;
            values[currentIndex * 2 + 1] = length;
         }

         uint frequency = (uint)loggingFrequencyUpDown.Value;
         int timeUnit = timeUnitComboBox.SelectedIndex;

         //int loggingState = loggingStateComboBox.SelectedIndex;

         bool eraseWhenDone = eraseWhenDoneCheckBox.Checked;
         bool pauseWhileDownloading = pauseDuringDownloadCheckBox.Checked;
         //int time = timeComboBox.SelectedIndex;
         decimal downloadTime = downloadTimeTextBox.Value;
         int downloadTimeUnits = downloadTimeUnitComboBox.SelectedIndex;
         int format = formatComboBox.SelectedIndex;
         bool accordingToRegisterFormat = this.accordingToRegisterRadioButton.Checked;
         bool hexadecimalFormat = this.hexadecimalRadioButton.Checked;
         bool decimalFormat = this.decimalRadioButton.Checked;

         return new Configuration
         {
            Values = values,
            Frequency = frequency,
            TimeUnit = timeUnit,
            EraseWhenDone = eraseWhenDone,
            PauseWhileDownloading = pauseWhileDownloading,
            DownloadTime = downloadTime,
            DownloadTimeUnits = downloadTimeUnits,
            FileFormat = format,
            RegisterFormat = (accordingToRegisterFormat ? 0 : (hexadecimalFormat ? 1 : 2))
         };
      }

      void LoadConfiguration(Configuration c)
      {
         int currentIndex = 0;
         for (currentIndex = 0; currentIndex < 15; ++currentIndex)
         {
            ushort address = c.Values[currentIndex * 2];
            ushort length = c.Values[currentIndex * 2 + 1];

            if (!addressComboBoxes[currentIndex].Enabled)
               continue;

            if (address == 0)
               addressComboBoxes[currentIndex].Text = "<None>";
            //--New Code
            else if (address == 1)
               addressComboBoxes[currentIndex].Text = "<Summary Registers>";
            //--
            else
               addressComboBoxes[currentIndex].Text = "0x" + address.ToString("X3");

            if (address != 0)
               lengthUpDowns[currentIndex].Enabled = true;
            lengthUpDowns[currentIndex].Maximum = FindMaximumLength(address);
            lengthUpDowns[currentIndex].Value = length;
         }

         loggingFrequencyUpDown.Value = c.Frequency;
         timeUnitComboBox.SelectedIndex = c.TimeUnit;

         eraseWhenDoneCheckBox.Checked = c.EraseWhenDone;
         pauseDuringDownloadCheckBox.Checked = c.PauseWhileDownloading;
         //timeComboBox.SelectedIndex = c.Time;
         downloadTimeTextBox.Value = c.DownloadTime;
         downloadTimeUnitComboBox.SelectedIndex = c.DownloadTimeUnits;
         formatComboBox.SelectedIndex = c.FileFormat;
         this.accordingToRegisterRadioButton.Checked = c.RegisterFormat == 0;
         this.hexadecimalRadioButton.Checked = c.RegisterFormat == 1;
         this.decimalRadioButton.Checked = c.RegisterFormat == 2;
      }

      void openButton_Click(object sender, EventArgs e)
      {
         var dialog = new OpenFileDialog();
         dialog.Filter = "XML Files (*.xml)|*.xml";
         if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

         openFile = dialog.FileName;
         Configuration configuration = Configuration.Load(openFile);
         LoadConfiguration(configuration);
      }

      void saveButton_Click(object sender, EventArgs e)
      {
         if (openFile == null)
         {
            var dialog = new SaveFileDialog();
            dialog.Filter = "XML Files (*.xml)|*.xml";
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
               return;

            openFile = dialog.FileName;
         }

         Configuration configuration = GetConfiguration();
         Configuration.Save(openFile, configuration);
      }

      void saveAsButton_Click(object sender, EventArgs e)
      {
         openFile = null;
         saveButton_Click(sender, e);
      }

      void syncButton_Click(object sender, EventArgs e)
      {
         String currentTime = DateTime.Now.ToShortDateString() + " " + DateTime.Now.AddSeconds(1).ToShortTimeString();
         clockTextBox.Text = currentTime;
         clockEdited = true;

         clockTextBox_LostFocus(sender, e);
      }

      void clockTextBox_KeyUp(object sender, KeyEventArgs e)
      {
         if (e.KeyCode == Keys.Enter)
         {
            clockTextBox_LostFocus(sender, new EventArgs());
            syncButton.Focus();
         }
         else if (e.KeyCode != Keys.LShiftKey && e.KeyCode != Keys.RShiftKey && e.KeyCode != Keys.Escape && e.KeyCode != Keys.Alt && e.KeyCode != Keys.ControlKey && e.KeyCode != Keys.Up && e.KeyCode != Keys.Down && e.KeyCode != Keys.Left && e.KeyCode != Keys.Right)
         {
            clockEdited = true;
         }
      }

      void clockTextBox_LostFocus(object sender, EventArgs e)
      {
         if (!clockEdited)
            return;

         try
         {
            ushort[] values = new ushort[2];
            timestampEncoder.EncodeObject(values, 0, 2, clockTextBox.Text);

            RegisterWrite write = new RegisterWrite(new RegisterBlockDefinition(0x4008, 2, 0x03), values);

            statusLabel.Text = "Setting clock...";
            outstandingWrites.Clear();
            outstandingWrites.Add(write);
            if (this.RegisterChanged != null)
               RegisterChanged(this, write);

            clockTextBox.BackColor = Color.White;
            clockEdited = false;
         }
         catch (FormatException)
         {
            statusLabel.Text = "Incorrect time format";
            clockTextBox.BackColor = Color.Red;
         }
      }
      //Adding/Creating Register List
      private static List<RegisterInformationEntry> CreateRegisterList(RegisterInformationEntry[] entries)
      {
         List<RegisterInformationEntry> list = new List<RegisterInformationEntry>(entries.Length + 1);
         list.Add(new RegisterInformationEntry(0, 0, "<None>", "", null, 0, 0, false));
         //--New Code
         //Adding new RegisterInformationEntry list item for Grouping
         list.Add(new RegisterInformationEntry(1, 0, "<Summary Registers>", "", null, 0, 0, false));
         //--
         for (int i = 0; i < entries.Length; ++i)
         {
            // Skip reserved items
            if (entries[i].Name == "(Reserved)")
               continue;

            list.Add(entries[i]);
         }

         for (int i = 0; i < entries.Length; ++i)
         {
            if (entries[i].Address >= 0x1100 && entries[i].Address < 0x1200)
            {
               list.Add(new RegisterInformationEntry((ushort)(entries[i].Address + 0x200), entries[i].Format, entries[i].Name + " (Revenue)", entries[i].ShortName, entries[i].Units, entries[i].Length, entries[i].DecimalPlaces, entries[i].Something));
            }
         }

         return list;
      }

      private void formatComboBox_SelectedIndexChanged(object sender, EventArgs e)
      {
         registerFormattingGroupBox.Enabled = (formatComboBox.SelectedIndex == 0);
      }

      public override void UpdateData(RegisterWrite write)
      {
         switch (state)
         {
            case State.Preparation:
               {
                  suppressAutoWrite = true;
                  loggingStateComboBox.SelectedIndex = 0;
                  suppressAutoWrite = false;

                  statusLabel.Text = "Starting download...";
                  state = State.ChangingPage;
                  ChangePage(page);
                  break;
               }
            case State.ChangingPage:
               {
                  if (write.Exception == null)
                  {
                     pageBeginning = true;
                     state = State.ReadingPage;
                     api.UpdateRegisters();
                  }
                  else
                  {
                     ChangePage(page);
                  }

                  break;
               }

            case State.ReadingPage:
               {
                  break;
               }

            case State.Standby:
               {
                  if (write.Exception != null)
                  {
                     if (write.Exception is Modbus.SlaveException)
                     {
                        Modbus.SlaveException se = write.Exception as Modbus.SlaveException;
                        if (se.Message.Contains("Exception Code 04"))
                        {
                           statusLabel.Text = "Device locked.";
                        }
                        else
                        {
                           statusLabel.Text = "Operation failed.";
                        }
                     }
                     else
                     {
                        statusLabel.Text = "Operation failed.";
                     }
                     break;
                  }

                  int outstandingCount = outstandingWrites.Count;
                  outstandingWrites.Remove(write);
                  if (outstandingWrites.Count == 0 && outstandingCount > 0)
                     statusLabel.Text = "Operation complete.";
                  break;
               }
         }
      }

      private void WriteLog(String filename, ushort[] log, int length)
      {

         if (formatComboBox.SelectedIndex == 1)
         {
            FileStream binaryFile = File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.Read);

            for (int i = 0; i < length; ++i)
            {
               binaryFile.WriteByte((byte)(log[i] >> 8));
               binaryFile.WriteByte((byte)(log[i] & 0xFF));
            }

            binaryFile.Close();
         }
         else
         {
            MemoryStream stream = new MemoryStream(length * 2);

            for (int i = 0; i < length; ++i)
            {
               stream.WriteByte((byte)(log[i] >> 8));
               stream.WriteByte((byte)(log[i] & 0xFF));
            }

            stream.Seek(0, SeekOrigin.Begin);

            LoggingConfiguration config = new LoggingConfiguration();
            for (int i = 0; i < 15; ++i)
               config.Blocks.Add(new RegisterBlockDefinition(GetAddress(i), (ushort)lengthUpDowns[i].Value, 0x03));

            RegisterFormat format = RegisterFormat.AccordingToRegister;
            if (decimalRadioButton.Checked)
               format = RegisterFormat.DecimalNumbers;
            else if (hexadecimalRadioButton.Checked)
               format = RegisterFormat.HexadecimalNumbers;

            Log logFile = new Log(config, stream, format);
            logFile.WriteCsv(filename);
            stream.Close();
         }
      }

      public uint CutoffTimestamp(uint latestEntry)
      {
         /* Possible values:
          * 0:  Entire Log
          * 1:  Hours
          * 2:  Days
          * 3:  Weeks
          * 4:  Months
          * 5:  Years
          */

         if (downloadTimeUnitComboBox.SelectedIndex == 0)
            return 0;

         DateTime epoch = new DateTime(1970, 1, 1);
         int timeValue = (int)downloadTimeTextBox.Value;
         TimeSpan duration;

         switch (downloadTimeUnitComboBox.SelectedIndex)
         {
            // hours
            case 1: duration = new TimeSpan(timeValue, 0, 0); break;

            // days
            case 2: duration = new TimeSpan(timeValue, 0, 0, 0); break;

            // weeks
            case 3: duration = new TimeSpan(7 * timeValue, 0, 0, 0); break;

            // months
            case 4:
               if (DateTime.Now.Month != 1)
                  duration = new TimeSpan(DateTime.DaysInMonth(DateTime.Now.Year, DateTime.Now.Month - 1) * timeValue, 0, 0, 0);
               else
                  duration = new TimeSpan(31 * timeValue, 0, 0, 0);
               break;

            // years
            case 5:
               duration = new TimeSpan(365 * timeValue, 0, 0, 0);
               break;

            default:
               return 0;
         }

         DateTime latestEntryDateTime = new DateTime(1970, 1, 1, 0, 0, 0).AddSeconds(latestEntry);

         uint converted = (uint)latestEntryDateTime.Subtract(duration).Subtract(epoch).TotalSeconds;
         return converted;
      }

      private bool nonDlWarningIssued = false;

      public override void UpdateData(DataBlockCollection data)
      {
         if (data.Count == 0)
            return;

         if (data.HasErrors)
         {
            foreach (DataBlock block in data)
            {
               SlaveException se = block.Exception as SlaveException;
               if (se != null && !nonDlWarningIssued)
               {
                  nonDlWarningIssued = true;
                  MessageBox.Show(
                     "The device to which you are connected does not appear to have a Display and Logging module installed.\n\nWattsOn meters with a Display and Logging module have a part number ending containing \"-DL\", and have an LCD screen.",
                     "Non-DL Meter",
                     MessageBoxButtons.OK,
                     MessageBoxIcon.Exclamation
                  );
                  return;
               }
            }
         }

         switch (state)
         {
            case State.ChangingPage:
               {
                  break;
               }

            case State.ReadingPage:
               {
                  int pageOffsetStart = pageBeginning ? 0 : 125;
                  ushort[] values;

                  try
                  {
                     values = data.GetData(pageBeginning ? logPageBeginningRegisters[0] : logPageEndingRegisters[0]);
                  }
                  catch
                  {
                     api.UpdateRegisters();
                     return;

                  }

                  for (int i = 0; i < 125; ++i)
                     memoryFile[writePosition++] = values[i];

                  while (readPosition <= writePosition - 2)
                  {
                     uint timestamp = (uint)((memoryFile[readPosition] << 15) | memoryFile[readPosition + 1]);

                     if (latestEntryTimestamp == 0)
                        latestEntryTimestamp = timestamp;

                     if ((uint)timestamp == (uint)0xFFFFFFFF || timestamp == 0 || timestamp <= CutoffTimestamp(latestEntryTimestamp) || (timestamp > previousTimestamp && previousTimestamp != 0))
                     {
                        if (progressBar.Maximum < entriesDownloaded)
                           progressBar.Maximum = entriesDownloaded;
                        progressBar.Value = entriesDownloaded;

                        statusLabel.Text = "Formatting log file...";

                        state = State.Standby;
                        WriteLog(saveFilename, memoryFile, readPosition);
                        statusLabel.Text = "Done (" + entriesDownloaded + " entries)";

                        try
                        {
                           progressBar.Style = ProgressBarStyle.Continuous;
                           progressBar.Value = progressBar.Maximum;

                        }
                        catch
                        {

                        }

                        if (eraseWhenDoneCheckBox.Checked)
                           eraseLogButton.PerformClick();

                        if (loggingPaused != 0)
                        {
                           RegisterWrite write = new RegisterWrite(new RegisterBlockDefinition(0x4013, 1, 0x03), new ushort[] { (ushort)loggingPaused });
                           this.loggingPaused = 0;
                           if (RegisterChanged != null)
                              RegisterChanged(this, write);
                        }

                        return;
                     }

                     previousTimestamp = timestamp;
                     entriesDownloaded++;
                     readPosition += entrySize;
                  }

                  try
                  {
                     if (downloadTimeUnitComboBox.SelectedIndex == 0)
                     {
                        progressBar.Style = ProgressBarStyle.Continuous;
                        if (progressBar.Maximum < entriesDownloaded)
                           progressBar.Maximum = entriesDownloaded;
                        progressBar.Value = entriesDownloaded;
                     }
                     else
                     {
                        progressBar.Style = ProgressBarStyle.Marquee;
                     }
                  }
                  catch (Exception)
                  {

                  }

                  uint downloadedSize = (uint)(entriesDownloaded * entrySize * 2);
                  uint totalSize = (uint)(entries * entrySize * 2);

                  if (downloadTimeUnitComboBox.SelectedIndex == 0)
                     statusLabel.Text = downloadedSize.ToByteSizeString() + " / " + totalSize.ToByteSizeString();
                  else
                     statusLabel.Text = downloadedSize.ToByteSizeString();

                  pageBeginning = !pageBeginning;
                  if (pageBeginning)
                  {
                     state = State.ChangingPage;
                     ChangePage(++page);
                  }
                  else
                  {
                     api.UpdateRegisters();
                  }

                  break;
               }

            case State.Standby:
               {
                  try
                  {
                     if (data.ContainsData(logSizeRegisters[0]))
                     {
                        ushort[] values = data.GetData(logSizeRegisters[0]);
                        entries = (uint)((values[0x0A] << 15) | values[0x0B]);
                        entrySize = values[0x0C];
                        uint size = (uint)(2 * entries * entrySize);
                        uint percentage = values[0x0D];

                        if (entries != 0)
                           logSizeValueLabel.Text = size.ToByteSizeString() + " (" + (percentage / 100.0).ToString("F2") + "% full)";
                        else
                           logSizeValueLabel.Text = "No log on device";

                        ushort mode = values[0x13];
                        suppressAutoWrite = true;
                        loggingStateComboBox.SelectedIndex = mode;
                        suppressAutoWrite = false;

                        if (!clockTextBox.Focused)
                        {
                           String timestampString = timestampEncoder.DecodeObject(values, 0x08, 2).ToString();
                           clockTextBox.Text = timestampString;
                           clockTextBox.BackColor = Color.White;
                           clockEdited = false;
                        }

                        lastUpdateTime = DateTime.Now.Ticks;
                     }

                     if (data.ContainsData(loggingConfigurationRegisters[0]))
                     {
                        loaded = true;

                        ushort[] values = data.GetData(loggingConfigurationRegisters[0]);

                        /*
                        uint timestamp = (uint)((values[0x08] << 16) | values[0x09]);
                        DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);
                        dateTime = dateTime.AddSeconds(timestamp);

                        if (dateTime.Year != DateTime.Now.Year)
                        {
                            DialogResult result = MessageBox.Show(
                                this,
                                "Warning: The clock on this unit does not appear to been set.\r\n" + 
                                " It is currently set to " + dateTime.ToString() + "\r\n\r\n" +
                                "Would you like to set it to match this computer's clock?",
                                "Clock Not Set",
                                MessageBoxButtons.YesNo
                            );

                            if (result == DialogResult.Yes)
                            {
                                timestamp = (uint) (DateTime.Now.Subtract(new DateTime(1970, 1, 1, 0, 0, 0, 0)).TotalSeconds + 1);

                                RegisterWrite write = new RegisterWrite(
                                    new RegisterBlockDefinition(0x4008, 2, 0x03),
                                    new ushort[] { 
                                        (ushort) (timestamp >> 16),
                                        (ushort) (timestamp & 0xFFFF)
                                    }
                                );

                                outstandingWrites.Add(write);
                                if (this.RegisterChanged != null)
                                    RegisterChanged(this, write);
                            }
                        }
                        */

                        uint frequency = (uint)((values[0xE] << 15) | values[0xF]);

                        if (frequency % 3600 == 0)
                        {
                           timeUnitComboBox.SelectedIndex = 2;
                           frequency /= 3600;
                        }
                        else if (frequency % 60 == 0)
                        {
                           timeUnitComboBox.SelectedIndex = 1;
                           frequency /= 60;
                        }
                        else
                        {
                           timeUnitComboBox.SelectedIndex = 0;
                        }

                        loggingFrequencyUpDown.Value = frequency;

                        // Setting the LoggingManager NumericUpDown Values
                        for (int i = 0; i < 15; ++i)
                        {
                           ushort address = values[0x20 + (2 * i)];
                           ushort length = values[0x20 + (2 * i) + 1];

                           if (address == 0)
                           {
                              addressComboBoxes[i].SelectedIndex = 0;
                              address_Changed(addressComboBoxes[i], new EventArgs(), i, false);
                              //Default Value
                              lengthUpDowns[i].Value = 0;
                           }
                           //--New Code--
                           else if (address == 1)
                           {
                              addressComboBoxes[i].SelectedIndex = 1;
                              address_Changed(addressComboBoxes[i], new EventArgs(), i, false);
                              //Default Value
                              lengthUpDowns[i].Value = 10;  //changed
                           }
                           //--
                           else
                           {
                              addressComboBoxes[i].Text = "0x" + address.ToString("X4");
                              address_Changed(addressComboBoxes[i], new EventArgs(), i, false);
                              //Outputs 2
                              //lengthUpDowns[i].Value = 7;
                              lengthUpDowns[i].Value = length; //changes the default value
                           }
                        }
                     }

                     UpdateEnabledComponents();
                     /*
                     for (int i = 0; i < 15; i++)
                     {
                        if (addressComboBoxes[i].SelectedIndex == 0)
                        {
                        lengthUpDowns[i].Value = 10;
                        }
                     }
                     */

                  }
                  catch (DataBlockCollection.NoDataException)
                  {

                  }
                  catch (Exception)
                  {

                  }

                  break;
               }
         }
      }

      public override IList<RegisterBlockDefinition> RequiredRegisters()
      {
         switch (state)
         {
            case State.ChangingPage:
               {
                  return null;
               }

            case State.ReadingPage:
               {
                  if (pageBeginning)
                  {
                     return logPageBeginningRegisters;
                  }
                  else
                  {
                     return logPageEndingRegisters;
                  }
               }

            case State.Standby:
               {
                  if (!loaded)
                     return loggingConfigurationRegisters;
                  else if (lastUpdateTime < DateTime.Now.Subtract(new TimeSpan(0, 0, 5)).Ticks)
                     return logSizeRegisters;
                  else
                     return null;
               }

            default:
               return null;
         }
      }

      private ushort GetAddress(int index, bool lenient = false)
      {
         //default value of Data Registers
         String text = addressComboBoxes[index].Text;
         if (text == "<None>")
            return 0;
         //--New Code
         if (text == "<Summary Registers>")
            return 1;
         //--

         String addressString = text;
         if (text.Contains(' '))
            addressString = text.Substring(0, text.IndexOf(' '));
         else
            addressString = text;

         if (lenient && (addressString == "0x" || addressString == "0X"))
            return 0;

         if (addressString.StartsWith("0x") || addressString.StartsWith("0X"))
            return ushort.Parse(addressString.Substring(2), System.Globalization.NumberStyles.HexNumber);
         else
            return ushort.Parse(addressString);
      }

      private void address_Changed(object sender, EventArgs e, int index, bool lenient)
      {
         bool validated;
         bool parsed = false;
         int maxLength = -1;
         ushort address = 0;
         try
         {
            address = GetAddress(index, lenient);
            maxLength = FindMaximumLength(address);
            validated = maxLength != -1;
            parsed = true;
         }
         catch (FormatException)
         {
            validated = false;
         }
         catch (OverflowException)
         {
            validated = false;
         }

         if (addressComboBoxes[index].Text == "<None>" || (parsed && address == 0))
         {
            lengthUpDowns[index].Maximum = 0;
            lengthUpDowns[index].Enabled = false;

         }
         //--New Code--
         else if (addressComboBoxes[index].Text == "<Summary Registers>")
         {
            //lengthUpDowns[index].Maximum = 5;
            lengthUpDowns[index].Enabled = true;
         }
         //--
         else
         {
            lengthUpDowns[index].Enabled = true;
         }

         writeButton.Enabled = validated && (!lenient || lengthUpDowns[index].Value <= maxLength);

         if (lenient)
         {
            validated = parsed;
         }

         if (!lenient && maxLength != -1)
         {
            lengthUpDowns[index].Maximum = maxLength;
         }

         addressComboBoxes[index].BackColor = validated ? Color.White : Color.Red;
         lengthUpDowns[index].BackColor = validated ? Color.White : Color.Red;

         //Summary Register Grouping
         //--New Code
         for (int i = 0; i < 15; i++)
         {
            if (addressComboBoxes[i].SelectedIndex == 1)
            {
               lengthUpDowns[index].Maximum = FindMaximumLength(1);
               lengthUpDowns[i].Value = 5;
            }
         }
         /*
         for (int i = 0; i < 15; i++)
         {
            if (addressComboBoxes[i].SelectedIndex == 0)
            {
               //lengthUpDowns[i].Text = "Auto";
               //lengthUpDowns[i].Value = "Auto";
            }
         }
         */
         //
      }

      private int FindMaximumLength(ushort address)
      {
         if (address == 0)
            return 0;

         //Summary Register Max Length
         if (address == 1)
            return 5;

         if (address >= 0x1300 && address < 0x1400)
            address -= 0x200;

         if (address >= 0x1000 && address < 0x1100)
            address += 0x100;

         if (address >= 0x1200 && address < 0x1300)
            address += 0x100;

         //if (address >= 0x100 && address < 0x200)
         //   address += 0x100;

         ushort objective = address;
         int count = 0;
         foreach (RegisterInformationEntry entry in DisplayRegisterDatabase.RegisterDatabase)
         {
            if (entry.Address <= objective && entry.Address + entry.Length > objective)
            {
               int offset = objective - entry.Address;
               objective += (ushort)(entry.Length - offset);
               count += (entry.Length - offset);
            }
         }

         if (count > 125)
            return 125;

         if (count == 0)
            return -1;

         return count;
      }

      private void readButton_Click(object sender, EventArgs e)
      {
         entries = 0;
         loaded = false;
      }

      private void UpdateEnabledComponents()
      {
         loggingFrequencyUpDown.Enabled = loggingStateComboBox.SelectedIndex == 0;
         timeUnitComboBox.Enabled = loggingStateComboBox.SelectedIndex == 0;
         writeButton.Enabled = loggingStateComboBox.SelectedIndex == 0;

         bool settingsChangeEnabled = (entries == 0 && loggingStateComboBox.SelectedIndex == 0);
         readButton.Enabled = settingsChangeEnabled;
         /*loggedAddressComboBox0.Enabled = settingsChangeEnabled;*/
         loggedAddressComboBox1.Enabled = settingsChangeEnabled;
         loggedAddressComboBox2.Enabled = settingsChangeEnabled;
         loggedAddressComboBox3.Enabled = settingsChangeEnabled;
         loggedAddressComboBox4.Enabled = settingsChangeEnabled;
         loggedAddressComboBox5.Enabled = settingsChangeEnabled;
         loggedAddressComboBox6.Enabled = settingsChangeEnabled;
         loggedAddressComboBox7.Enabled = settingsChangeEnabled;
         loggedAddressComboBox8.Enabled = settingsChangeEnabled;
         loggedAddressComboBox9.Enabled = settingsChangeEnabled;
         loggedAddressComboBox10.Enabled = settingsChangeEnabled;
         loggedAddressComboBox11.Enabled = settingsChangeEnabled;
         loggedAddressComboBox12.Enabled = settingsChangeEnabled;
         loggedAddressComboBox13.Enabled = settingsChangeEnabled;
         loggedAddressComboBox14.Enabled = settingsChangeEnabled;
         loggedAddressComboBox15.Enabled = settingsChangeEnabled;
         /*loggedLengthUpDown0.Enabled = settingsChangeEnabled;*/
         loggedLengthUpDown1.Enabled = settingsChangeEnabled;
         loggedLengthUpDown2.Enabled = settingsChangeEnabled;
         loggedLengthUpDown3.Enabled = settingsChangeEnabled;
         loggedLengthUpDown4.Enabled = settingsChangeEnabled;
         loggedLengthUpDown5.Enabled = settingsChangeEnabled;
         loggedLengthUpDown6.Enabled = settingsChangeEnabled;
         loggedLengthUpDown7.Enabled = settingsChangeEnabled;
         loggedLengthUpDown8.Enabled = settingsChangeEnabled;
         loggedLengthUpDown9.Enabled = settingsChangeEnabled;
         loggedLengthUpDown10.Enabled = settingsChangeEnabled;
         loggedLengthUpDown11.Enabled = settingsChangeEnabled;
         loggedLengthUpDown12.Enabled = settingsChangeEnabled;
         loggedLengthUpDown13.Enabled = settingsChangeEnabled;
         loggedLengthUpDown14.Enabled = settingsChangeEnabled;
         loggedLengthUpDown15.Enabled = settingsChangeEnabled;

         eraseLogButton.Enabled = (entries != 0 && loggingStateComboBox.SelectedIndex == 0);
         downloadLogGroupBox.Enabled = (entries != 0);
      }

      private void loggingStateComboBox_SelectedIndexChanged(object sender, EventArgs e)
      {
         UpdateEnabledComponents();

         if (!suppressAutoWrite)
         {
            loaded = false;

            ushort[] values = new ushort[1];
            values[0] = (ushort)loggingStateComboBox.SelectedIndex;

            statusLabel.Text = "Changing log state...";

            RegisterWrite write = new RegisterWrite(new RegisterBlockDefinition(0x4013, 1, 0x03), values);
            outstandingWrites.Clear();
            outstandingWrites.Add(write);

            if (this.RegisterChanged != null)
               this.RegisterChanged(this, write);
         }
      }

      private void timeUnitComboBox_SelectedIndexChanged(object sender, EventArgs e)
      {
         if (timeUnitComboBox.SelectedIndex == 0)
         {
            loggingFrequencyUpDown.Maximum = 65535;
         }
         else if (timeUnitComboBox.SelectedIndex == 1)
         {
            loggingFrequencyUpDown.Maximum = 1092;
         }
         else
         {
            loggingFrequencyUpDown.Maximum = 18;
         }
      }

      private void writeButton_Click(object sender, EventArgs e)
      {
         if (entries != 0 && loggingStateComboBox.SelectedIndex == 0)
         {
            try
            {
               uint frequency = (uint)loggingFrequencyUpDown.Value;
               if (timeUnitComboBox.SelectedIndex == 1)
                  frequency *= 60;
               else if (timeUnitComboBox.SelectedIndex == 2)
                  frequency *= 3600;

               ushort[] frequencyValues = new ushort[2];
               frequencyValues[0] = (ushort)(frequency >> 15);
               frequencyValues[1] = (ushort)(frequency & 0xFFFF);

               RegisterWrite frequencyWrite = new RegisterWrite(new RegisterBlockDefinition(0x400E, 2, 0x03), frequencyValues);
               outstandingWrites.Add(frequencyWrite);
               if (RegisterChanged != null)
                  RegisterChanged(this, frequencyWrite);
            }
            catch (Exception ex)
            {
               MessageBox.Show("Could not write to device: " + ex.Message);
            }
         }
         else
         {

            int currentIndex = 0;
            try
            {
               ushort[] values = new ushort[32];
               for (currentIndex = 0; currentIndex < 15; ++currentIndex)
               {
                  ushort address = GetAddress(currentIndex);
                  ushort length = (ushort)lengthUpDowns[currentIndex].Value;

                  if (length > FindMaximumLength(address))
                     throw new FormatException("Block is too large.");

                  if (length == 0)
                  {
                     /* length = Look up the actual length that it's supposed to be */

                     int index = -1;
                     for (int i = 0; i < DisplayRegisterDatabase.RegisterDatabase.Length; ++i)
                     {
                        if (DisplayRegisterDatabase.RegisterDatabase[i].Address == address)
                        {
                           index = i;
                           break;
                        }
                     }

                     if (index != -1)
                     {
                        int defaultLength = DisplayRegisterDatabase.RegisterDatabase[index].Length;  //length of RegisterDatabase
                        length = (ushort)defaultLength;
                     }
                  }

                  values[currentIndex * 2] = address;
                  values[currentIndex * 2 + 1] = length;
               }

               statusLabel.Text = "Writing configuration...";

               RegisterWrite configWrite = new RegisterWrite(new RegisterBlockDefinition(0x4020, 32, 0x03), values);
               outstandingWrites.Clear();
               outstandingWrites.Add(configWrite);
               if (RegisterChanged != null)
                  RegisterChanged(this, configWrite);

               uint frequency = (uint)loggingFrequencyUpDown.Value;
               if (timeUnitComboBox.SelectedIndex == 1)
                  frequency *= 60;
               else if (timeUnitComboBox.SelectedIndex == 2)
                  frequency *= 3600;

               ushort[] frequencyValues = new ushort[2];
               frequencyValues[0] = (ushort)(frequency >> 15);
               frequencyValues[1] = (ushort)(frequency & 0xFFFF);

               RegisterWrite frequencyWrite = new RegisterWrite(new RegisterBlockDefinition(0x400E, 2, 0x03), frequencyValues);
               outstandingWrites.Add(frequencyWrite);
               if (RegisterChanged != null)
                  RegisterChanged(this, frequencyWrite);
            }
            catch (Exception)
            {
               MessageBox.Show("Could not write to device: The register block in slot " + currentIndex + " is not a valid register block for this device.");
            }
         }
      }

      private void eraseLogButton_Click(object sender, EventArgs e)
      {
         ushort[] values = new ushort[1];
         values[0] = 0xA5A5;

         statusLabel.Text = "Erasing log...";

         RegisterWrite write = new RegisterWrite(new RegisterBlockDefinition(0x4015, 1, 0x03), values);
         outstandingWrites.Clear();
         outstandingWrites.Add(write);

         if (this.RegisterChanged != null)
            RegisterChanged(this, write);

         loaded = false;
      }

      private void downloadButton_Click(object sender, EventArgs e)
      {
         SaveFileDialog saveDialog = new SaveFileDialog();
         saveDialog.AddExtension = true;

         if (formatComboBox.SelectedIndex == 0)
         {
            saveDialog.DefaultExt = ".csv";
            saveDialog.Filter = "Comma-Separated Value Files (*.csv)|*.csv|All Files (*.*)|*.*";
         }
         else
         {
            saveDialog.DefaultExt = ".bin";
            saveDialog.Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*";
         }

         if (DialogResult.OK != saveDialog.ShowDialog())
            return;

         this.saveFilename = saveDialog.FileName;

         // Test that we can actually write to this filename before getting too far ahead of ourselves
         try
         {
            Stream test = File.Open(saveFilename, FileMode.Create, FileAccess.Write, FileShare.None);
            test.WriteByte(0);
            test.Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Unable to write to file " + saveFilename + ": " + ex.Message, "Error Saving File");
            return;
         }

         memoryFile = new ushort[5000000];
         pageBeginning = true;
         readPosition = 0;
         writePosition = 0;
         //page = 3000;
         page = 0;
         previousTimestamp = 0;
         entriesDownloaded = 0;
         latestEntryTimestamp = 0;
         progressBar.Maximum = (int)entries;

         if (pauseDuringDownloadCheckBox.Checked)
         {
            statusLabel.Text = "Pausing log...";
            state = State.Preparation;
            this.loggingPaused = this.loggingStateComboBox.SelectedIndex;
            RegisterWrite write = new RegisterWrite(new RegisterBlockDefinition(0x4013, 1, 0x03), new ushort[] { 0 });
            if (RegisterChanged != null)
               RegisterChanged(this, write);
         }
         else
         {
            statusLabel.Text = "Starting download...";
            state = State.ChangingPage;
            ChangePage(page);
         }
      }

      private void ChangePage(int page)
      {
         RegisterWrite changePage = new RegisterWrite(new RegisterBlockDefinition(0x4010, 1, 0x03), new ushort[] { (ushort)page });

         if (this.RegisterChanged != null)
            this.RegisterChanged(this, changePage);
      }

      private void registerFormattingGroupBox_Enter(object sender, EventArgs e)
      {

      }
   }

   static class Extensions
   {
      public static String ToByteSizeString(this uint length)
      {
         const double KB = 1024;
         const double MB = 1024 * KB;
         const double GB = 1024 * MB;
         if (length >= GB)
         {
            return (length / GB).ToString("0.00") + " GB";
         }
         else if (length >= MB)
         {
            return (length / MB).ToString("0.00") + " MB";
         }
         else if (length >= KB)
         {
            return (length / KB).ToString("0.00") + " KB";
         }
         return length.ToString() + " B";
      }
   }

   /*
   public class LoggingManagerEx : LoggingManager
   {
      public LoggingManagerEx()
      {
      }

      //Line 772 
      //public override void UpdateData(DataBlockCollection data)
      public override void UpdateData(RegisterWrite write)
      {
         //if numericUpDown is 0 then set is as "Auto"
         //Default value as Auto
         this.Text = "Auto";
      }
   }
   */

   /*
   public void UpdateData(NumericUpDown numericUpDown)
   {
      numericUpDown.Text = "Auto";
      //base.UpdateData(text);

      int autoValue;
      ushort address;

      for (int i = 0; i < 15; i++)
      {
         if (lengthUpDowns[i].Text == "Auto")
         {
            address = GetAddress(i);
            autoValue = FindMaximumLength(address);
         }
      }
   }
   */

   //NumericUpDown is a control
   public class CustomNumericUpDown : NumericUpDown
   {
      public CustomNumericUpDown()
      {
      }

      protected override void UpdateEditText()
      {
         // Custom display-value when value is 0
         this.Text = this.Value == 0 ? "Auto" : this.Value.ToString();
      }
   }

   class Program
   {
      static void Main(string[] args)
      {
         var n = new CustomNumericUpDown();
         n.Value = 0;

         MessageBox.Show(n.Text);
      }
   }


}
