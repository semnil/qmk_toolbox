﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;
using System.IO;
using QMK_Toolbox.Properties;
using System.Runtime.InteropServices;
using System.Security.Permissions;

namespace QMK_Toolbox {
    using HidLibrary;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Management;
    using System.Text.RegularExpressions;

    public partial class Form1 : Form {
        BackgroundWorker backgroundWorker1;

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007; //device changed
        private const int deviceIDOffset = 70;

        Flashing f;

        [SecurityPermission(SecurityAction.LinkDemand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt32() == DBT_DEVNODES_CHANGED) {
                //Print("*** USB change\n");
            }
            base.WndProc(ref m);
        }

        List<HidDevice> _devices = new List<HidDevice>();

        public void P(string str, MessageType type) {
            if (!InvokeRequired) {
                Printing.color(richTextBox1, Printing.format(richTextBox1, str, type), type);
            } else {
                this.Invoke(new Action<string, MessageType>(P), new object[] { str, type });
            }
        }
        public void R(string str, MessageType type) {
            if (!InvokeRequired) {
                Printing.colorResponse(richTextBox1, Printing.formatResponse(richTextBox1, str, type), type);
            } else {
                this.Invoke(new Action<string, MessageType>(R), new object[] { str, type });
            }
        }

        public string getTarget() {
            return targetBox.Text;
        }

        public string getHexFile() {
            return hexFileBox.Text;
        }

        public Form1() {
            InitializeComponent();

            f = new Flashing(this);
            
            richTextBox1.Cursor = Cursors.Arrow; // mouse cursor like in other controls

            backgroundWorker1 = new BackgroundWorker();
            backgroundWorker1.DoWork += backgroundWorker1_DoWork;
            backgroundWorker1.WorkerReportsProgress = true;
        }

        private void richTextBox1_TextChanged(object sender, EventArgs e) {
            // This shouldn't be needed anymore
            // set the current caret position to the end
            // richTextBox1.SelectionStart = richTextBox1.Text.Length;
            // scroll it automatically
            // richTextBox1.ScrollToCaret();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e) {
            ArrayList arraylist = new ArrayList(this.hexFileBox.Items);
            Settings.Default.hexFileCollection = arraylist;
            Settings.Default.Save();
        }

        private void Form1_Load(object sender, EventArgs e) {
            backgroundWorker1.RunWorkerAsync();

            var lines = File.ReadLines(f.mcuListPath);
            foreach (var line in lines) {
                string[] tokens = line.Split(',');
                targetBox.Items.Add(tokens[0]);
            }

            if (Settings.Default.hexFileCollection != null)
                this.hexFileBox.Items.AddRange(Settings.Default.hexFileCollection.ToArray());

            P("QMK Toolbox supports the following bootloaders:", MessageType.Info);
            R(" - DFU (Atmel, LUFA)\n", MessageType.Info);
            R(" - Caterina (Arduino, Pro Micros)\n", MessageType.Info);
            R(" - Halfkay (Teensy, Ergodox EZ)\n", MessageType.Info);

            f.SetupDFU();
            f.SetupAvrdude();
            f.SetupTeensy();
            f.SetupDfuUtil();

            List<USBDeviceInfo> devices = new List<USBDeviceInfo>();

            ManagementObjectCollection collection;
            using (var searcher = new ManagementObjectSearcher(@"SELECT * FROM Win32_PnPEntity where DeviceID Like ""USB%"""))
                collection = searcher.Get();

            DetectBootloaderFromCollection(collection);

            UpdateHIDDevices();

        }

        private void UpdateHIDDevices() {
            List<HidDevice> devices = new List<HidDevice>(GetListableDevices());

            foreach (HidDevice device in devices) {
                bool device_exists = false;
                foreach (HidDevice existing_device in _devices) {
                    device_exists |= existing_device.DevicePath.Equals(device.DevicePath);
                }
                if (device != null && !device_exists) {
                    _devices.Add(device);
                    device.OpenDevice();
                    //device.Inserted += DeviceAttachedHandler;
                    //device.Removed += DeviceRemovedHandler;

                    device.MonitorDeviceEvents = true;

                    P(("Connecting to HID device: " + GetManufacturerString(device) + " - " + GetProductString(device) + " ").PadRight(deviceIDOffset, '-') + 
                        " | " + device.Attributes.VendorHexId + ":" + device.Attributes.ProductHexId, MessageType.HID);
                    R("Parent ID Prefix: " + GetParentIDPrefix(device) + "\n", MessageType.Info);

                    device.ReadReport(OnReport);
                    device.CloseDevice();
                }
            }
            foreach (HidDevice existing_device in _devices) {
                bool device_exists = false;
                foreach (HidDevice device in devices) {
                    device_exists |= existing_device.DevicePath.Equals(device.DevicePath);
                }
                if (!device_exists) {
                    P(("Disconnected from HID device: " + GetManufacturerString(existing_device) + " - " + GetProductString(existing_device) + " ").PadRight(deviceIDOffset, '-') + 
                    " | " + existing_device.Attributes.VendorHexId + ":" + existing_device.Attributes.ProductHexId, MessageType.HID);
                    R("Parent ID Prefix: " + GetParentIDPrefix(existing_device) + "\n", MessageType.Info);
                }
            }
            _devices = devices;
        }

        private string GetParentIDPrefix(HidDevice d) {
            Regex regex = new Regex("#([&0-9a-fA-F]+)#");
            var vp = regex.Match(d.DevicePath);
            return vp.Groups[1].ToString();
        }
        
        private bool DetectBootloaderFromCollection(ManagementObjectCollection collection, bool connected = true) {
            bool found = false;
            foreach (var instance in collection) {
                found = DetectBootloader(instance, connected);
            }
            return found;
        }

        private bool DetectBootloader(ManagementBaseObject instance, bool connected = true) {
            string connected_string = connected ? "connected" : "disconnected";

            // Detects Atmel Vendor ID
            var dfu = Regex.Match(instance.GetPropertyValue("DeviceID").ToString(), @".*VID_03EB.*");
            // Detects Arduino Vendor ID
            var caterina = Regex.Match(instance.GetPropertyValue("DeviceID").ToString(), @".*VID_2341.*");
            // Detects PJRC Vendor ID
            var halfkay_vid = Regex.Match(instance.GetPropertyValue("DeviceID").ToString(), @".*VID_16C0.*");
            var halfkay_pid = Regex.Match(instance.GetPropertyValue("DeviceID").ToString(), @".*PID_0478.*");
            var halfkay_nohid = Regex.Match(instance.GetPropertyValue("Name").ToString(), @".*USB.*");
            var dfuUtil_pid = Regex.Match(instance.GetPropertyValue("DeviceID").ToString(), @".*VID_0483.*");
            var dfuUtil_vid = Regex.Match(instance.GetPropertyValue("DeviceID").ToString(), @".*PID_DF11.*");

            Regex deviceid_regex = new Regex("VID_([0-9A-F]+).*PID_([0-9A-F]+)");
            var vp = deviceid_regex.Match(instance.GetPropertyValue("DeviceID").ToString());
            string VID = vp.Groups[1].ToString();
            string PID = vp.Groups[2].ToString();

            string device_name;
            if (dfu.Success) {
                device_name = "DFU";
                f.dfuAvailable = connected;
            } else if (caterina.Success) {
                device_name = "Caterina";
                Regex regex = new Regex("(COM[0-9]+)");
                var v = regex.Match(instance.GetPropertyValue("Name").ToString());
                f._COM = v.Groups[1].ToString();
                f.caterinaAvailable = connected;
            } else if (halfkay_vid.Success && halfkay_pid.Success && halfkay_nohid.Success) {
                device_name = "Halfkay";
                f.halfkayAvailable = connected;
            } else if (dfuUtil_pid.Success && dfuUtil_vid.Success) {
                device_name = "Dfu Util";
                f.dfuUtilAvailable = connected;
            } else {
                return false;
            }

            P((device_name + " device " + connected_string + ": " + instance.GetPropertyValue("Name") + " ").PadRight(deviceIDOffset, '-') + " | 0x" + VID + ":0x" + PID, MessageType.Bootloader);
            return true;
        }

        private IEnumerable<HidDevice> GetListableDevices() {
            var devices = HidDevices.Enumerate();
            List<HidDevice> listenable_devices = new List<HidDevice>();
            foreach (HidDevice device in devices) {
                if ((ushort)device.Capabilities.UsagePage == Flashing.UsagePage)
                    listenable_devices.Add(device);
            }
            return listenable_devices;
        }

        private string GetProductString(HidDevice d) {
            if (d == null)
                return "";
            byte[] bs;
            d.ReadProduct(out bs);
            string ps = "";
            foreach (byte b in bs) {
                if (b > 0)
                    ps += ((char)b).ToString();
            }
            return ps;
        }

        private string GetManufacturerString(HidDevice d) {
            if (d == null)
                return "";
            byte[] bs;
            d.ReadManufacturer(out bs);
            string ps = "";
            foreach (byte b in bs) {
                if (b > 0)
                    ps += ((char)b).ToString();
            }
            return ps;
        }

        private void DeviceAttachedHandler() {
            // not sure if this will be useful
        }

        private void DeviceRemovedHandler() {
            // not sure if this will be useful
        }

        private void OnReport(HidReport report) {

            var data = report.Data;
            //Print(string.Format("* recv {0} bytes:", data.Length));

            string outputString = string.Empty;
            for (int i = 0; i < data.Length; i++) {
                outputString += (char)data[i];
                if (i % 16 == 15 && i < data.Length) {
                    R(outputString, MessageType.HID);
                    outputString = string.Empty;
                }
            }

            foreach (HidDevice device in _devices) {
                device.ReadReport(OnReport);
            }
        }

        private void FlashButton_Click(object sender, EventArgs e) {
            if (!InvokeRequired) {
                button1.Enabled = false;
                button3.Enabled = false;
                f.Flash();
                button1.Enabled = true;
                button3.Enabled = true;
            } else {
                this.Invoke(new Action<object, EventArgs>(FlashButton_Click), new object[] { sender, e });
            }
        }

        private void button2_Click(object sender, EventArgs e) {
            if (openFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                hexFileBox.Text = openFileDialog1.FileName;
                hexFileBox.Items.Add(hexFileBox.Text);
            }
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e) {
            Settings.Default.Save();
        }

        private void DeviceInsertedEvent(object sender, EventArrivedEventArgs e) {
            ManagementBaseObject instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];

            if (DetectBootloader(instance) && checkBox1.Checked) {
                FlashButton_Click(sender, e);
            }
            UpdateHIDDevices();
        }

        private void DeviceRemovedEvent(object sender, EventArrivedEventArgs e) {
            ManagementBaseObject instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];

            DetectBootloader(instance, false);
            UpdateHIDDevices();
        }

        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e) {
            WqlEventQuery insertQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");

            ManagementEventWatcher insertWatcher = new ManagementEventWatcher(insertQuery);
            insertWatcher.EventArrived += new EventArrivedEventHandler(DeviceInsertedEvent);
            insertWatcher.Start();

            WqlEventQuery removeQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");
            ManagementEventWatcher removeWatcher = new ManagementEventWatcher(removeQuery);
            removeWatcher.EventArrived += new EventArrivedEventHandler(DeviceRemovedEvent);
            removeWatcher.Start();

            // Do something while waiting for events
            System.Threading.Thread.Sleep(2000000);
        }

        private void button3_Click(object sender, EventArgs e) {
            button3.Enabled = false;
            f.Reset();
            button3.Enabled = true;
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e) {
            if (checkBox1.Checked) {
                P("Auto-flash enabled", MessageType.Info);
                button1.Enabled = false;
                button3.Enabled = false;
            } else {
                P("Auto-flash disabled", MessageType.Info);
                button1.Enabled = true;
                button3.Enabled = true;
            }
        }

        // Set the button's status tip.
        private void btn_MouseEnter(object sender, EventArgs e) {
            Control obj = sender as Control;
            toolStripStatusLabel1.Text = obj.Tag.ToString();
        }

        // Remove the button's status tip.
        private void btn_MouseLeave(object sender, EventArgs e) {
            //if (toolStripStatusLabel1.Text.Equals((sender as Control).Tag)) {
            //    toolStripStatusLabel1.Text = "";
            //}
        }

        private void button4_Click(object sender, EventArgs e) {
            ((Button)sender).Enabled = false;
            foreach (HidDevice device in _devices) {
                device.CloseDevice();
            }
            P("Listing compatible HID devices: (must have Usage Page "+ string.Format("0x{0:X4}", Flashing.UsagePage) + ")", MessageType.HID);
            foreach (HidDevice device in _devices) {
                if (device != null) {
                    device.OpenDevice();
                    R((" - " + GetManufacturerString(device) + " - " + GetProductString(device) + " ").PadRight(deviceIDOffset, '-') + 
                    " | " + device.Attributes.VendorHexId + ":" + device.Attributes.ProductHexId + "\n", MessageType.Info);
                    R("   Parent ID Prefix: " + GetParentIDPrefix(device) + "\n", MessageType.Info);
                }
                device.CloseDevice();
            }
            ((Button)sender).Enabled = true;
        }

        private void ReportWritten(bool success) {
            if (!InvokeRequired) {
                button5.Enabled = true;
                button6.Enabled = true;
                if (success) {
                    R("Report sent sucessfully\n", MessageType.Info);
                } else {
                    R("Report errored\n", MessageType.Error);
                }
            } else {
                this.Invoke(new Action<bool>(ReportWritten), new object[] { success });
            }
        }

        private void button5_Click(object sender, EventArgs e) {
            button5.Enabled = false;
            foreach (HidDevice device in _devices) {
                device.CloseDevice();
            }
            foreach (HidDevice device in _devices) {
                device.OpenDevice();
                //device.Write(Encoding.ASCII.GetBytes("BOOTLOADER"), 0);
                byte[] data = new byte[2];
                data[0] = 0;
                data[1] = 0xFE;
                HidReport report = new HidReport(2, new HidDeviceData(data, HidDeviceData.ReadStatus.Success));
                device.WriteReport(report, ReportWritten);
                device.CloseDevice();
                P("Sending report", MessageType.HID);
            }
        }

        private void button6_Click(object sender, EventArgs e) {
            button6.Enabled = false;
            foreach (HidDevice device in _devices) {
                device.CloseDevice();
            }
            foreach (HidDevice device in _devices) {
                device.OpenDevice();
                //device.Write(Encoding.ASCII.GetBytes("BOOTLOADER"), 0);
                byte[] data = new byte[2];
                data[0] = 0;
                data[1] = 0x01;
                HidReport report = new HidReport(2, new HidDeviceData(data, HidDeviceData.ReadStatus.Success));
                device.WriteReport(report, ReportWritten);
                device.CloseDevice();
                P("Sending report", MessageType.HID);
            }
        }
    }

    class USBDeviceInfo {
        public USBDeviceInfo(string deviceID, string pnpDeviceID, string description) {
            this.DeviceID = deviceID;
            this.PnpDeviceID = pnpDeviceID;
            this.Description = description;
        }
        public string DeviceID { get; private set; }
        public string PnpDeviceID { get; private set; }
        public string Description { get; private set; }
    }
}
