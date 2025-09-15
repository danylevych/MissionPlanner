using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MissionPlanner.Utilities;
using MissionPlanner.Controls;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;
using MissionPlanner;
using System.Drawing;
using GMap.NET.WindowsForms;
using MissionPlanner.GCSViews;
using MissionPlanner.Maps;


namespace speedbartest
{
    public class Plugin : MissionPlanner.Plugin.Plugin
    {

        public override string Name
        {
            get { return "Speed Bar Test"; }
        }

        public override string Version
        {
            get { return "0.1"; }
        }

        public override string Author
        {
            get { return "Test"; }
        }

        public override bool Init()
        {
            return true;
        }

        public override bool Loaded()
        {
            var rootbut = new ToolStripMenuItem("Speed Test");
            ToolStripItemCollection col = Host.FDMenuHud.Items;
            col.Add(rootbut);

            var speedBarTest = new ToolStripMenuItem("Test Speed Bar");
            speedBarTest.Click += (s, e) =>
            {
                FlightData.myhud.displayspeedbar = !FlightData.myhud.displayspeedbar;
                MessageBox.Show("Speed Bar is now: " + FlightData.myhud.displayspeedbar);
            };
            rootbut.DropDownItems.Add(speedBarTest);

            var speedNumbersTest = new ToolStripMenuItem("Test Speed Numbers");
            speedNumbersTest.Click += (s, e) =>
            {
                FlightData.myhud.displayspeednumbers = !FlightData.myhud.displayspeednumbers;
                MessageBox.Show("Speed Numbers is now: " + FlightData.myhud.displayspeednumbers);
            };
            rootbut.DropDownItems.Add(speedNumbersTest);

            return true;
        }

        public override bool Loop()
        {
            return true;
        }

        public override bool Exit()
        {
            return true;
        }
    }
}
