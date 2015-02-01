using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Xml.Linq;

namespace PreviewToy {

  public partial class PreviewToyHandler : Form {
    private Dictionary<IntPtr, Preview> previews;
    private DispatcherTimer dispatcherTimer;

    private IntPtr active_client_handle = (IntPtr)0;
    private String active_client_title = "";

    private Dictionary<String, Dictionary<String, ClientLocation>> unique_layouts;
    private Dictionary<String, ClientLocation> flat_layout;
    private Dictionary<String, ClientLocation> client_layout;

    private bool is_initialized;

    private Stopwatch ignoring_size_sync;

    Dictionary<string, string> xml_bad_to_ok_chars;

    [DllImport("user32.dll")]
    private static extern int GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    private struct Rect {
      public int Left;
      public int Top;
      public int Right;
      public int Bottom;
    }

    public struct ClientLocation {
      public int X;
      public int Y;
      public int Width;
      public int Height;
    }

    public enum zoom_anchor_t {
      NW = 0,
      N,
      NE,
      W,
      C,
      E,
      SW,
      S,
      SE
    };

    private Dictionary<zoom_anchor_t, RadioButton> zoom_anchor_button_map;

    public PreviewToyHandler() {
      is_initialized = false;

      previews = new Dictionary<IntPtr, Preview>();

      xml_bad_to_ok_chars = new Dictionary<string, string>();
      xml_bad_to_ok_chars["<"] = "---lt---";
      xml_bad_to_ok_chars["&"] = "---amp---";
      xml_bad_to_ok_chars[">"] = "---gt---";
      xml_bad_to_ok_chars["\""] = "---quot---";
      xml_bad_to_ok_chars["\'"] = "---apos---";
      xml_bad_to_ok_chars[","] = "---comma---";
      xml_bad_to_ok_chars["."] = "---dot---";

      unique_layouts = new Dictionary<String, Dictionary<String, ClientLocation>>();
      flat_layout = new Dictionary<String, ClientLocation>();
      client_layout = new Dictionary<String, ClientLocation>();

      ignoring_size_sync = new Stopwatch();
      ignoring_size_sync.Start();

      InitializeComponent();
      init_options();

      //  DispatcherTimer setup
      dispatcherTimer = new DispatcherTimer();
      dispatcherTimer.Tick += new EventHandler(dispatcherTimer_Tick);
      dispatcherTimer.Interval = new TimeSpan(0, 0, 1);
      dispatcherTimer.Start();

      is_initialized = true;

      previews_check_listbox.DisplayMember = "Text";

    }


    private void GlassForm_Load(object sender, EventArgs e) {
      refresh_thumbnails();
      this.Resize += PreviewToyHandler_Resize;
      label_version.Text = Assembly.GetExecutingAssembly().GetName().Version.ToString();
    }

    void PreviewToyHandler_Resize(object sender, EventArgs e) {
      if (FormWindowState.Minimized == this.WindowState && checkBox_toTray.Checked) {
        this.Hide();
      }
    }


    private void init_options() {
      option_zoom_on_hover.Checked = Properties.Settings.Default.zoom_on_hover;
      zoom_anchor_button_map = new Dictionary<zoom_anchor_t, RadioButton>();
      zoom_anchor_button_map[zoom_anchor_t.NW] = option_zoom_anchor_NW;
      zoom_anchor_button_map[zoom_anchor_t.N] = option_zoom_anchor_N;
      zoom_anchor_button_map[zoom_anchor_t.NE] = option_zoom_anchor_NE;
      zoom_anchor_button_map[zoom_anchor_t.W] = option_zoom_anchor_W;
      zoom_anchor_button_map[zoom_anchor_t.C] = option_zoom_anchor_C;
      zoom_anchor_button_map[zoom_anchor_t.E] = option_zoom_anchor_E;
      zoom_anchor_button_map[zoom_anchor_t.SW] = option_zoom_anchor_SW;
      zoom_anchor_button_map[zoom_anchor_t.S] = option_zoom_anchor_S;
      zoom_anchor_button_map[zoom_anchor_t.SE] = option_zoom_anchor_SE;
      zoom_anchor_button_map[(zoom_anchor_t)Properties.Settings.Default.zoom_anchor].Checked = true;
      option_zoom_factor.Text = Properties.Settings.Default.zoom_amount.ToString();

      option_always_on_top.Checked = Properties.Settings.Default.always_on_top;
      option_hide_active.Checked = Properties.Settings.Default.hide_active;
      option_hide_all_if_not_right_type.Checked = Properties.Settings.Default.hide_all;

      option_unique_layout.Checked = Properties.Settings.Default.unique_layout;

      option_sync_size.Checked = Properties.Settings.Default.sync_resize;
      option_sync_size_x.Text = Properties.Settings.Default.sync_resize_x.ToString();
      option_sync_size_y.Text = Properties.Settings.Default.sync_resize_y.ToString();

      option_show_thumbnail_frames.Checked = Properties.Settings.Default.show_thumb_frames;

      option_show_overlay.Checked = Properties.Settings.Default.show_overlay;

      option_track_client_windows.Checked = Properties.Settings.Default.track_client_windows;

      checkBox_toTray.Checked = Properties.Settings.Default.toTray;

      // disable/enable zoom suboptions
      option_zoom_factor.Enabled = Properties.Settings.Default.zoom_on_hover;
      foreach (var kv in zoom_anchor_button_map) {
        kv.Value.Enabled = Properties.Settings.Default.zoom_on_hover;
      }

      load_layout();
    }


    private void spawn_and_kill_previews() {
      if (!is_initialized) { return; }

      //Process[] processes = Process.GetProcessesByName("ExeFile");
      //Process[] processes2 = 
      List<Process> processes = new List<Process>();
      processes.AddRange(Process.GetProcessesByName("ExeFile"));
      processes.AddRange(Process.GetProcessesByName("firefox"));
      processes.AddRange(Process.GetProcessesByName("chrome"));

      List<IntPtr> processHandles = new List<IntPtr>();

      // pop new previews

      foreach (Process process in processes) {
        processHandles.Add(process.MainWindowHandle);

        Size sync_size = new Size();
        sync_size.Width = (int)Properties.Settings.Default.sync_resize_x;
        sync_size.Height = (int)Properties.Settings.Default.sync_resize_y;
        Size size_to_use = sync_size;

        if (!previews.ContainsKey(process.MainWindowHandle) && process.MainWindowTitle != "") {
          if (flat_layout.ContainsKey(process.MainWindowTitle)) {
            size_to_use = new Size(flat_layout[process.MainWindowTitle].Width, flat_layout[process.MainWindowTitle].Height);
          }
          previews[process.MainWindowHandle] = new Preview(process.MainWindowHandle, "...", this, size_to_use);
          previews[process.MainWindowHandle].set_render_area_size(size_to_use);

          // apply more thumbnail specific options
          previews[process.MainWindowHandle].MakeTopMost(Properties.Settings.Default.always_on_top);
          set_thumbnail_frame_style(previews[process.MainWindowHandle], Properties.Settings.Default.show_thumb_frames);

          // add a preview also
          previews_check_listbox.BeginUpdate();
          previews_check_listbox.Items.Add(previews[process.MainWindowHandle]);
          previews_check_listbox.SetItemChecked(previews_check_listbox.Items.Count - 1, !process.ProcessName.Contains("exefile"));
          previews_check_listbox.EndUpdate();

          refresh_client_window_locations(process);
        } else if (previews.ContainsKey(process.MainWindowHandle) && process.MainWindowTitle != previews[process.MainWindowHandle].Text) //or update the preview titles
                {
          previews[process.MainWindowHandle].SetLabel(process.MainWindowTitle);
          refresh_client_window_locations(process);
        }

        if (process.MainWindowHandle == DwmApi.GetForegroundWindow()) {
          active_client_handle = process.MainWindowHandle;
          active_client_title = process.MainWindowTitle;
        }

      }

      // clean up old previews
      List<IntPtr> to_be_pruned = new List<IntPtr>();
      foreach (IntPtr processHandle in previews.Keys) {
        if (!(processHandles.Contains(processHandle))) {
          to_be_pruned.Add(processHandle);
        }
      }

      foreach (IntPtr processHandle in to_be_pruned) {
        previews_check_listbox.BeginUpdate();
        previews_check_listbox.Items.Remove(previews[processHandle]);
        previews_check_listbox.EndUpdate();

        previews[processHandle].Close();
        previews.Remove(processHandle);
      }

      previews_check_listbox.Update();

    }

    private void refresh_client_window_locations(Process process) {
      if (Properties.Settings.Default.track_client_windows && client_layout.ContainsKey(process.MainWindowTitle)) {
        MoveWindow(
            process.MainWindowHandle,
            client_layout[process.MainWindowTitle].X,
            client_layout[process.MainWindowTitle].Y,
            client_layout[process.MainWindowTitle].Width,
            client_layout[process.MainWindowTitle].Height,
            true);
      }
    }


    private string remove_nonconform_xml_characters(string entry) {
      foreach (var kv in xml_bad_to_ok_chars) {
        entry = entry.Replace(kv.Key, kv.Value);
      }
      return entry;
    }

    private string restore_nonconform_xml_characters(string entry) {
      foreach (var kv in xml_bad_to_ok_chars) {
        entry = entry.Replace(kv.Value, kv.Key);
      }
      return entry;
    }

    private XElement MakeXElement(string elementName, string attributeName, string input) {
      string clean = remove_nonconform_xml_characters(elementName).Replace(" ", "_");
      XElement el = new XElement(clean);
      el.SetAttributeValue(attributeName, input);
      return el;
    }
    private XElement MakeXElement(string input) {
      string clean = remove_nonconform_xml_characters(input).Replace(" ", "_");
      return new XElement(clean);
    }

    private string ParseXElement(XElement input, String attributeName = "") {
      if (attributeName == "") {
        return restore_nonconform_xml_characters(input.Name.ToString()).Replace("_", " ");
      } else {
        return input.Attribute(attributeName).Value;
      }
    }

    #region Load layouts
    private void load_layout() {
      load_unique_layout("layout.xml");
      load_flat_layout("flat_layout.xml");
      load_client_layout("client_layout.xml");
    }

    private void load_client_layout(string FileName = "client_layout.xml") {
      if (!File.Exists(FileName)) {
        return;
      }

      XElement docElement = XElement.Load(FileName);
      XElement rootElement = docElement.Elements().Where(r => r.Attribute("machine") != null && r.Attribute("machine").Value == Environment.MachineName).FirstOrDefault();
      if (rootElement == null) {
        return;
      }

      foreach (var el in rootElement.Elements()) {
        ClientLocation clientLocation = new ClientLocation();
        clientLocation.X = Convert.ToInt32(el.Element("x").Value);
        clientLocation.Y = Convert.ToInt32(el.Element("y").Value);
        clientLocation.Width = (el.Element("width") == null ? 124 : Convert.ToInt32(el.Element("width").Value));
        clientLocation.Height = (el.Element("height") == null ? 124 : Convert.ToInt32(el.Element("height").Value));

        client_layout[ParseXElement(el, "client")] = clientLocation;
      }
    }

    private void load_flat_layout(string FileName = "flat_layout.xml") {
      if (!File.Exists(FileName)) {
        return;
      }
      XElement docElement = XElement.Load(FileName);
      XElement rootElement = docElement.Elements().Where(r => r.Attribute("machine") != null && r.Attribute("machine").Value == Environment.MachineName).FirstOrDefault();
      if (rootElement == null) {
        return;
      }

      foreach (var el in rootElement.Elements()) {
        ClientLocation clientLocation = new ClientLocation();
        clientLocation.X = Convert.ToInt32(el.Element("x").Value);
        clientLocation.Y = Convert.ToInt32(el.Element("y").Value);
        clientLocation.Width = (el.Element("width") == null ? 124 : Convert.ToInt32(el.Element("width").Value));
        clientLocation.Height = (el.Element("height") == null ? 124 : Convert.ToInt32(el.Element("height").Value));

        flat_layout[ParseXElement(el, "client")] = clientLocation;
      }
    }

    private void load_unique_layout(string FileName = "layout.xml") {
      if (!File.Exists(FileName)) {
        return;
      }
      XElement docElement = XElement.Load(FileName);
      XElement rootElement = docElement.Elements().Where(r => r.Attribute("machine") != null && r.Attribute("machine").Value == Environment.MachineName).FirstOrDefault();
      if (rootElement == null) {
        return;
      }

      foreach (var el in rootElement.Elements()) {
        Dictionary<String, ClientLocation> inner = new Dictionary<String, ClientLocation>();
        foreach (var inner_el in el.Elements()) {
          ClientLocation clientLocation = new ClientLocation();
          clientLocation.X = Convert.ToInt32(inner_el.Element("x").Value);
          clientLocation.Y = Convert.ToInt32(inner_el.Element("y").Value);
          clientLocation.Width = (inner_el.Element("width") == null ? 124 : Convert.ToInt32(inner_el.Element("width").Value));
          clientLocation.Height = (inner_el.Element("height") == null ? 124 : Convert.ToInt32(inner_el.Element("height").Value));

          inner[ParseXElement(inner_el, "client")] = clientLocation;
        }
        unique_layouts[ParseXElement(el, "title")] = inner;
      }
    }
    #endregion

    #region Store layouts
    private void store_layout() {
      store_unique_layout();
      store_flat_layout();
      store_client_layout();
    }

    private void store_client_layout(String FileName = "client_layout.xml") {
      XElement root = null;
      XElement machineNode = null;
      if (File.Exists(FileName)) {
        root = XElement.Load(FileName);
        machineNode = root.Elements("layout").Where(r => r.Attribute("machine").Value == Environment.MachineName).FirstOrDefault();
      } else {
        root = new XElement("layouts");
      }

      if (machineNode == null) {
        machineNode = new XElement("layout");
        machineNode.SetAttributeValue("machine", Environment.MachineName);
        root.Add(machineNode);
      }
      machineNode.RemoveNodes();

      foreach (var clientKV in client_layout) {
        if (clientKV.Key == "" || clientKV.Key == "...") {
          continue;
        }
        XElement position = MakeXElement("position", "client", clientKV.Key);
        position.Add(new XElement("x", clientKV.Value.X));
        position.Add(new XElement("y", clientKV.Value.Y));
        position.Add(new XElement("width", clientKV.Value.Width));
        position.Add(new XElement("height", clientKV.Value.Height));
        machineNode.Add(position);
      }

      root.Save(FileName);
    }

    private void store_flat_layout(String FileName = "flat_layout.xml") {
      XElement root = null;
      XElement machineNode = null;
      if (File.Exists(FileName)) {
        root = XElement.Load(FileName);
        machineNode = root.Elements("layout").Where(r => r.Attribute("machine").Value == Environment.MachineName).FirstOrDefault();
      } else {
        root = new XElement("layouts");
      }

      if (machineNode == null) {
        machineNode = new XElement("layout");
        machineNode.SetAttributeValue("machine", Environment.MachineName);
        root.Add(machineNode);
      }
      machineNode.RemoveNodes();

      foreach (var clientKV in flat_layout) {
        if (clientKV.Key == "" || clientKV.Key == "...") {
          continue;
        }
        XElement position = MakeXElement("position", "client", clientKV.Key);
        position.Add(new XElement("x", clientKV.Value.X));
        position.Add(new XElement("y", clientKV.Value.Y));
        position.Add(new XElement("width", clientKV.Value.Width));
        position.Add(new XElement("height", clientKV.Value.Height));
        machineNode.Add(position);
      }

      root.Save(FileName);
    }

    private void store_unique_layout(String FileName = "layout.xml") {
      XElement root = null;
      XElement machineNode = null;
      if (File.Exists(FileName)) {
        root = XElement.Load(FileName);
        machineNode = root.Elements("layout").Where(r => r.Attribute("machine").Value == Environment.MachineName).FirstOrDefault();
      } else {
        root = new XElement("layouts");
      }

      if (machineNode == null) {
        machineNode = new XElement("layout");
        machineNode.SetAttributeValue("machine", Environment.MachineName);
        root.Add(machineNode);
      }
      machineNode.RemoveNodes();

      foreach (var client in unique_layouts.Keys) {
        if (client == "") {
          continue;
        }
        XElement characterNode = MakeXElement("client", "title", client);
        foreach (var thumbnail_ in unique_layouts[client]) {
          String thumbnail = thumbnail_.Key;
          if (thumbnail == "" || thumbnail == "...") {
            continue;
          }
          XElement position = MakeXElement("position", "client", thumbnail);
          position.Add(new XElement("x", thumbnail_.Value.X));
          position.Add(new XElement("y", thumbnail_.Value.Y));
          position.Add(new XElement("width", thumbnail_.Value.Width));
          position.Add(new XElement("height", thumbnail_.Value.Height));
          characterNode.Add(position);
        }
        machineNode.Add(characterNode);
      }
      root.Save(FileName);
    }
    #endregion

    #region Handle layouts
    private void handle_unique_layout(Preview preview, String last_known_active_window) {
      Dictionary<String, ClientLocation> layout;
      if (unique_layouts.TryGetValue(last_known_active_window, out layout)) {
        ClientLocation new_loc;
        if (Properties.Settings.Default.unique_layout && layout.TryGetValue(preview.Text, out new_loc)) {
          preview.doMove(new_loc);
        } else {
          // create inner dict
          ClientLocation clientLocation = new ClientLocation();
          clientLocation.X = preview.Location.X;
          clientLocation.Y = preview.Location.Y;
          clientLocation.Width = preview.Size.Width;
          clientLocation.Height = preview.Size.Height;
          layout[preview.Text] = clientLocation;
        }
      } else if (last_known_active_window != "") {
        // create outer dict
        ClientLocation clientLocation = new ClientLocation();
        clientLocation.X = preview.Location.X;
        clientLocation.Y = preview.Location.Y;
        clientLocation.Width = preview.Size.Width;
        clientLocation.Height = preview.Size.Height;

        unique_layouts[last_known_active_window] = new Dictionary<String, ClientLocation>();
        unique_layouts[last_known_active_window][preview.Text] = clientLocation;
      }
    }
    
    private void handle_flat_layout(Preview preview) {
      ClientLocation layout;
      if (flat_layout.TryGetValue(preview.Text, out layout)) {
        preview.doMove(layout);
      } else if (preview.Text != "") {
        ClientLocation clientLocation = new ClientLocation();
        clientLocation.X = preview.Location.X;
        clientLocation.Y = preview.Location.Y;
        clientLocation.Width = preview.Width;
        clientLocation.Height = preview.Height;

        flat_layout[preview.Text] = clientLocation;
        store_layout();
      }
    }
    #endregion

    private void update_client_locations() {
      Process[] processes = Process.GetProcessesByName("ExeFile");
      List<IntPtr> processHandles = new List<IntPtr>();

      foreach (Process process in processes) {
        Rect rect = new Rect();
        GetWindowRect(process.MainWindowHandle, out rect);

        int left = Math.Abs(rect.Left);
        int right = Math.Abs(rect.Right);
        int client_width = Math.Abs(left - right);

        int top = Math.Abs(rect.Top);
        int bottom = Math.Abs(rect.Bottom);
        int client_height = Math.Abs(top - bottom);

        ClientLocation clientLocation = new ClientLocation();
        clientLocation.X = rect.Left;
        clientLocation.Y = rect.Top;
        clientLocation.Width = client_width;
        clientLocation.Height = client_height;


        client_layout[process.MainWindowTitle] = clientLocation;
      }
    }

    public void preview_did_switch() {
      update_client_locations();
      store_layout(); //todo: check if it actually changed ...
      foreach (KeyValuePair<IntPtr, Preview> entry in previews) {
        entry.Value.MakeTopMost(Properties.Settings.Default.always_on_top);
      }
    }

    private bool window_is_preview_or_client(IntPtr window) {
      bool active_window_is_right_type = false;
      foreach (KeyValuePair<IntPtr, Preview> entry in previews) {
        if (entry.Key == window || entry.Value.Handle == window || this.Handle == window || entry.Value.overlay.Handle == window) {
          active_window_is_right_type = true;
        }
      }
      return active_window_is_right_type;
    }

    private void refresh_thumbnails() {

      IntPtr active_window = DwmApi.GetForegroundWindow();

      // hide, show, resize and move
      foreach (KeyValuePair<IntPtr, Preview> entry in previews) {
        if (!window_is_preview_or_client(active_window) && Properties.Settings.Default.hide_all) {
          entry.Value.Hide();
        } else if (entry.Key == active_client_handle && Properties.Settings.Default.hide_active) {
          entry.Value.Hide();
        } else {
          entry.Value.Show();
          if (Properties.Settings.Default.unique_layout) {
            handle_unique_layout(entry.Value, active_client_title);
          } else {
            handle_flat_layout(entry.Value);
          }
        }
        entry.Value.hover_zoom = Properties.Settings.Default.zoom_on_hover;
        entry.Value.show_overlay = Properties.Settings.Default.show_overlay;
      }

      DwmApi.DwmIsCompositionEnabled();
    }

    public void syncronize_preview_size(Size sync_size) {
      if (!is_initialized) { return; }

      if (Properties.Settings.Default.sync_resize &&
          Properties.Settings.Default.show_thumb_frames &&
          ignoring_size_sync.ElapsedMilliseconds > 500) {
        ignoring_size_sync.Stop();

        option_sync_size_x.Text = sync_size.Width.ToString();
        option_sync_size_y.Text = sync_size.Height.ToString();

        foreach (KeyValuePair<IntPtr, Preview> entry in previews) {
          if (entry.Value.Handle != DwmApi.GetForegroundWindow()) {
            entry.Value.set_render_area_size(sync_size);
          }
        }

      }

    }

    public void register_preview_position(String preview_title, Point position, Size size) {

      if (Properties.Settings.Default.unique_layout) {
        Dictionary<String, ClientLocation> layout;
        if (unique_layouts.TryGetValue(active_client_title, out layout)) {
          ClientLocation clientLocation = new ClientLocation();
          clientLocation.X = position.X;
          clientLocation.Y = position.Y;
          clientLocation.Width = size.Width;
          clientLocation.Height = size.Height;
          layout[preview_title] = clientLocation;
        } else if (active_client_title == "") {
          unique_layouts[active_client_title] = new Dictionary<String, ClientLocation>();
          ClientLocation clientLocation = new ClientLocation();
          clientLocation.X = position.X;
          clientLocation.Y = position.Y;
          clientLocation.Width = size.Width;
          clientLocation.Height = size.Height;

          unique_layouts[active_client_title][preview_title] = clientLocation;
        }
      } else {
        ClientLocation clientLocation = new ClientLocation();
        clientLocation.X = position.X;
        clientLocation.Y = position.Y;
        clientLocation.Width = size.Width;
        clientLocation.Height = size.Height;
        flat_layout[preview_title] = clientLocation;
      }
      store_layout();
    }

    private void dispatcherTimer_Tick(object sender, EventArgs e) {
      spawn_and_kill_previews();
      refresh_thumbnails();
      if (ignoring_size_sync.ElapsedMilliseconds > 500) { ignoring_size_sync.Stop(); };

      if (DwmApi.DwmIsCompositionEnabled()) {
        aero_status_label.Text = "AERO is ON";
        aero_status_label.ForeColor = Color.Black;
      } else {
        aero_status_label.Text = "AERO is OFF";
        aero_status_label.ForeColor = Color.Red;
      }

    }

    #region GUI events
    private void option_hide_all_if_noneve_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.hide_all = option_hide_all_if_not_right_type.Checked;
      Properties.Settings.Default.Save();
      refresh_thumbnails();
    }

    private void option_unique_layout_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.unique_layout = option_unique_layout.Checked;
      Properties.Settings.Default.Save();
      refresh_thumbnails();
    }

    private void option_hide_active_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.hide_active = option_hide_active.Checked;
      Properties.Settings.Default.Save();
      refresh_thumbnails();
    }

    private void option_sync_size_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.sync_resize = option_sync_size.Checked;
      Properties.Settings.Default.Save();
      refresh_thumbnails();
    }

    private void parse_size_entry() {
      UInt32 x = 0, y = 0;

      try {
        y = Convert.ToUInt32(option_sync_size_y.Text);
        x = Convert.ToUInt32(option_sync_size_x.Text);
      } catch (System.FormatException) {
        return;
      }

      if (x < 64 || y < 64) {
        return;
      }

      Properties.Settings.Default.sync_resize_y = y;
      Properties.Settings.Default.sync_resize_x = x;
      Properties.Settings.Default.Save();

      // resize
      syncronize_preview_size(new Size((int)Properties.Settings.Default.sync_resize_x,
                                       (int)Properties.Settings.Default.sync_resize_y));
    }

    private void option_sync_size_x_TextChanged(object sender, EventArgs e) {
      parse_size_entry();
    }

    private void option_sync_size_y_TextChanged(object sender, EventArgs e) {
      parse_size_entry();
    }

    private void option_always_on_top_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.always_on_top = option_always_on_top.Checked;
      Properties.Settings.Default.Save();
      refresh_thumbnails();
    }
    
    private void option_show_thumbnail_frames_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.show_thumb_frames = option_show_thumbnail_frames.Checked;
      Properties.Settings.Default.Save();

      if (Properties.Settings.Default.show_thumb_frames) {
        ignoring_size_sync.Stop();
        ignoring_size_sync.Reset();
        ignoring_size_sync.Start();
      }

      foreach (var thumbnail in previews) {
        set_thumbnail_frame_style(thumbnail.Value, Properties.Settings.Default.show_thumb_frames);
      }
    }

    private void list_running_clients_SelectedIndexChanged(object sender, EventArgs e) { }

    private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
      AboutBox aboutBox = new AboutBox();
      aboutBox.Show();
    }

    private void option_zoom_on_hover_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.zoom_on_hover = option_zoom_on_hover.Checked;
      Properties.Settings.Default.Save();
      refresh_thumbnails();
      option_zoom_factor.Enabled = Properties.Settings.Default.zoom_on_hover;
      if (is_initialized) {
        foreach (var kv in zoom_anchor_button_map) {
          kv.Value.Enabled = Properties.Settings.Default.zoom_on_hover;
        }
      }
    }

    private void option_show_overlay_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.show_overlay = option_show_overlay.Checked;
      Properties.Settings.Default.Save();
      refresh_thumbnails();
    }

    private void option_zoom_anchor_X_CheckedChanged(object sender, EventArgs e) {
      handle_zoom_anchor_setting();
      Properties.Settings.Default.Save();
    }

    private void option_zoom_factor_TextChanged(object sender, EventArgs e) {
      try {
        float tmp = (float)Convert.ToDouble(option_zoom_factor.Text);
        if (tmp < 1) {
          tmp = 1;
        } else if (tmp > 10) {
          tmp = 10;
        }
        Properties.Settings.Default.zoom_amount = tmp;
        option_zoom_factor.Text = tmp.ToString();
        Properties.Settings.Default.Save();
      } catch {
        // do naught
      }
    }

    private void checkedListBox1_SelectedIndexChanged(object sender, EventArgs e) {
      refresh_thumbnails();
    }

    private void checkedListBox1_SelectedIndexChanged2(object sender, EventArgs e) {
      System.Windows.Forms.ItemCheckEventArgs arg = (System.Windows.Forms.ItemCheckEventArgs)e;
      ((Preview)this.previews_check_listbox.Items[arg.Index]).MakeHidden(arg.NewValue == System.Windows.Forms.CheckState.Checked);
      refresh_thumbnails();
    }

    private void flowLayoutPanel1_Paint(object sender, PaintEventArgs e) {

    }

    private void checkBox1_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.track_client_windows = option_track_client_windows.Checked;
      Properties.Settings.Default.Save();
      refresh_thumbnails();
    }

    private void checkBox_toTray_CheckedChanged(object sender, EventArgs e) {
      Properties.Settings.Default.toTray = checkBox_toTray.Checked;
      Properties.Settings.Default.Save();
    }

    private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e) {
      this.Show();
    }

    #endregion

    void set_thumbnail_frame_style(Preview preview, bool show_frames) {
      if (show_frames) {
        preview.FormBorderStyle = FormBorderStyle.SizableToolWindow;
      } else {
        preview.FormBorderStyle = FormBorderStyle.None;
      }
    }

    private void previewToyMainBindingSource_CurrentChanged(object sender, EventArgs e) {

    }

    private void handle_zoom_anchor_setting() {
      foreach (var kv in zoom_anchor_button_map) {
        if (kv.Value.Checked == true)
          Properties.Settings.Default.zoom_anchor = (byte)kv.Key;
      }
    }

  }
}