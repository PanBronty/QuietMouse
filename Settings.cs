using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

sealed class Settings : Form
{
    sealed class MonitorInfo
    {
        public string Id, Name;
        public float Width, Height;
        public decimal InitialHeight;
        public NumericUpDown HeightInput;
        public decimal DefaultTop;
        public NumericUpDown Input;
        public RectangleF Bounds;
        public MonitorInfo(string id, string name, float width, float height, decimal top)
        { Id=id; Name=name; Width=width; Height=height; InitialHeight=(decimal)height; DefaultTop=top; }
    }
    sealed class Canvas : Panel { public Canvas() { DoubleBuffered=true; ResizeRedraw=true; } }
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32")] static extern bool IsIconic(IntPtr w);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr w,int command);
    [DllImport("user32")] static extern IntPtr SendMessageTimeout(IntPtr w,int msg,IntPtr a,IntPtr b,uint flags,uint timeout,out IntPtr result);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern uint GetPrivateProfileString(string section,string key,string fallback,StringBuilder result,int size,string file);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern bool WritePrivateProfileString(string section,string key,string value,string file);
    [DllImport("user32")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    const string Title="QuietMouse · Display settings";
    static readonly string FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuietMouse","Profiles.ini");
    readonly string profile;
    readonly MonitorInfo[] screens;
    readonly CheckBox bridge=new CheckBox{Text="Cross unmatched edges",AutoSize=true};
    readonly CheckBox shortcut=new CheckBox{Text="Pause shortcut (Ctrl+Alt+Shift+Q)",AutoSize=true};
    readonly Label warning=Caption("",9,false);
    readonly Label runtime=Caption("",9,false);
    bool dirty,refreshQueued;
    string[] nextLayout;
    sealed class Draft{
        public decimal[] Tops,Heights;
        public bool Bridge,Shortcut;
    }
    static readonly Dictionary<string,Draft> drafts=new Dictionary<string,Draft>();
    static string[] Detect(){
        var start=new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"QuietMouse.exe"),"--layout"){
            UseShellExecute=false,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.UTF8,CreateNoWindow=true
        };
        using(var child=Process.Start(start)){
            string result=child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            if(child.ExitCode!=0)throw new InvalidDataException("Windows could not read the display configuration.");
            return result.TrimEnd().Split('\n');
        }
    }
    readonly Canvas canvas=new Canvas();
    readonly Label saved=Caption("",9,false);
    static readonly Color paper=Color.FromArgb(22,29,32), surface=Color.FromArgb(35,46,49), text=Color.FromArgb(229,239,236);
    readonly Color ink=Color.FromArgb(27,53,54), mint=Color.FromArgb(120,234,201);
    float scale,origin;
    int selected=1;
    float density;
    int D(float n){return (int)Math.Round(n*density);}
    int dragging=-1,anchor;
    decimal start;

    static Label Caption(string text, float size, bool bold)
    {
        return new Label {Text=text,AutoSize=true, Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular),
            ForeColor=Settings.text, Anchor=AnchorStyles.Left};
    }
    Settings(string[] layout)
    {
        var header=layout[0].TrimEnd().Split('\t');
        profile=header[0];
        bool ready=header[1]=="row";
        screens=layout.Skip(1).Select(line=>{
            var v=line.TrimEnd().Split('\t');
            var culture=CultureInfo.InvariantCulture;
            return new MonitorInfo(v[0],v[1]+(v[5]=="estimate"?" (estimated)":""),float.Parse(v[2],culture),float.Parse(v[3],culture),decimal.Parse(v[4],culture));
        }).ToArray();
        selected=0;
        Text=Title; Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font=new Font("Segoe UI",10); BackColor=paper; ForeColor=text;
        AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
        using(var g=CreateGraphics())density=g.DpiX/96f;
        AutoScaleMode=AutoScaleMode.None;
        StartPosition=FormStartPosition.CenterScreen; AutoScroll=true;
        ClientSize=new Size(D(860),Math.Min(D(765),Screen.FromControl(this).WorkingArea.Height-D(64)));
        MinimumSize=SizeFromClientSize(new Size(D(665),D(420)));
        int dark=1;
        DwmSetWindowAttribute(Handle,20,ref dark,sizeof(int));
        var root=new TableLayoutPanel { Dock=DockStyle.Top,Height=Math.Max(D(670),ClientSize.Height),Padding=new Padding(D(20),D(10),D(20),D(10)),ColumnCount=1,RowCount=10 };
        Resize+=(o,e)=>root.Height=Math.Max(D(670),ClientSize.Height);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach(float n in new float[]{40,36,0,32,148,44,34,32,30,48})
            root.RowStyles.Add(n==0?new RowStyle(SizeType.Percent,100):new RowStyle(SizeType.Absolute,D(n)));
        Controls.Add(root);
        root.Controls.Add(Caption("Display alignment",21,true),0,0);
        root.Controls.Add(Caption(ready?"Drag each display to match its position on your desk. Positive offsets move it down.":"Cursor alignment is paused. Arrange your displays side by side in Windows Extend mode.",10,false),0,1);
        canvas.Dock=DockStyle.Fill; canvas.BackColor=ink; canvas.Paint+=PaintLayout;
        canvas.MouseDown+=StartDrag; canvas.MouseMove+=MoveDrag;
        canvas.MouseUp+=(o,e)=>canvas.Capture=false;
        canvas.MouseCaptureChanged+=(o,e)=>{if(!canvas.Capture){dragging=-1;canvas.Invalidate();}};
        root.Controls.Add(canvas,0,2);
        root.Controls.Add(Caption("Display calibration",12,true),0,3);
        var fields=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=screens.Length,RowCount=1,Margin=Padding.Empty,AutoScroll=true};
        root.Controls.Add(fields,0,4);
        for(int i=0;i<screens.Length;i++){
            var s=screens[i]; fields.ColumnStyles.Add(screens.Length<=3?new ColumnStyle(SizeType.Percent,100f/screens.Length):new ColumnStyle(SizeType.Absolute,D(230)));
            var field=new NumericUpDown {Minimum=-10000,Maximum=10000,DecimalPlaces=2,Increment=0.5m,Width=D(100),
                BackColor=paper,ForeColor=text,BorderStyle=BorderStyle.FixedSingle,
                AccessibleName=s.Name+" – top offset in millimeters"};
            field.Value=s.DefaultTop; s.Input=field;
            field.ValueChanged+=(o,e)=>Changed();
            int index=i;
            var card=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,BackColor=surface,
                Margin=new Padding(D(3),0,D(3),0),Padding=new Padding(D(9),D(5),D(4),D(5))};
            card.Paint+=(o,e)=>{
                using(var stripe=new SolidBrush(index==selected?mint:Color.FromArgb(64,83,83)))
                    e.Graphics.FillRectangle(stripe,0,0,card.Width,D(2));
            };
            field.Enter+=(o,e)=>{selected=index;canvas.Invalidate();fields.Invalidate(true);};
            var name=Caption((i+1)+"   "+s.Name,10,true);
            card.Controls.Add(name,0,0);
            var line=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
            field.Width=D(88);line.Controls.Add(field);line.Controls.Add(Caption("top offset (mm)",8,false));card.Controls.Add(line,0,1);
            var sizeField=new NumericUpDown{Minimum=1,Maximum=10000,DecimalPlaces=2,Increment=0.5m,Value=s.InitialHeight,
                Width=D(88),BackColor=paper,ForeColor=text,AccessibleName=s.Name+" – screen height in millimeters"};
            s.HeightInput=sizeField;
            float ratio=s.Width/s.Height;
            sizeField.ValueChanged+=(o,e)=>{s.Height=(float)sizeField.Value;s.Width=s.Height*ratio;Changed();};
            sizeField.Enter+=(o,e)=>{selected=index;canvas.Invalidate();fields.Invalidate(true);};
            var sizeRow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
            sizeRow.Controls.Add(sizeField);sizeRow.Controls.Add(Caption("height (mm)",8,false));card.Controls.Add(sizeRow,0,2);
            var parts=layout[i+1].TrimEnd().Split('\t');
            decimal detected=decimal.Parse(parts[6],CultureInfo.InvariantCulture);
            var reset=new Button{Text=(parts[5]=="estimate"?"Use estimate":"Use detected height")+" · "+detected.ToString("0.##")+" mm",
                FlatStyle=FlatStyle.Flat,AutoSize=true,ForeColor=mint,Font=new Font("Segoe UI",8)};
            reset.FlatAppearance.BorderSize=0;
            reset.Click+=(o,e)=>sizeField.Value=Math.Min(sizeField.Maximum,Math.Max(sizeField.Minimum,detected));
            card.Controls.Add(reset,0,3);
            fields.Controls.Add(card,i,0);
        }
        root.Controls.Add(Caption("Same error along the edge? Adjust top offset. Drift from top to bottom? Check screen height.",9,false),0,5);
        var options=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        bridge.Checked=header.Length>2 && header[2]=="1";
        var value=new StringBuilder(8);
        GetPrivateProfileString("General","PauseShortcut","0",value,8,FileName);
        shortcut.Checked=value.ToString()=="1";
        bridge.CheckedChanged+=(o,e)=>Changed();shortcut.CheckedChanged+=(o,e)=>Changed();
        options.Controls.Add(bridge);options.Controls.Add(shortcut);root.Controls.Add(options,0,6);
        warning.ForeColor=Color.Khaki;root.Controls.Add(warning,0,7);
        runtime.ForeColor=mint;root.Controls.Add(runtime,0,8);
        var footer=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,8,0,0)};
        var save=new Button{Text="Save changes",Size=new Size(D(124),D(34)),BackColor=mint,ForeColor=paper,FlatStyle=FlatStyle.Flat};
        save.FlatAppearance.BorderSize=0;
        var cancel=new Button{Text="Close",Size=new Size(D(88),D(34)),BackColor=surface,ForeColor=text,FlatStyle=FlatStyle.Flat};
        cancel.Click+=(o,e)=>Close();
        cancel.FlatAppearance.BorderSize=0;
        saved.ForeColor=mint;
        save.Click+=(o,e)=>Save(); footer.Controls.Add(save);footer.Controls.Add(cancel);footer.Controls.Add(saved);root.Controls.Add(footer,0,9);
        var help=new Button{Text="Help",AccessibleDescription="Open the QuietMouse guide (F1)",Size=new Size(D(72),D(34)),BackColor=surface,ForeColor=text,FlatStyle=FlatStyle.Flat};
        help.FlatAppearance.BorderSize=0;
        help.Click+=(o,e)=>ShowGuide();
        footer.Controls.Add(help);
        HelpRequested+=(o,e)=>{e.Handled=true;ShowGuide();};
        AcceptButton=save;CancelButton=cancel;
        Draft draft;
        if(drafts.TryGetValue(profile,out draft)){
            for(int i=0;i<screens.Length;i++){screens[i].Input.Value=draft.Tops[i];screens[i].HeightInput.Value=draft.Heights[i];}
            bridge.Checked=draft.Bridge;shortcut.Checked=draft.Shortcut;dirty=true;
            saved.Text="Draft restored";
        }else if(drafts.Count>0)saved.Text="Previous layout draft kept";
        UpdateWarning();
        Activated+=(o,e)=>ShowStatus(false);
    }
    void ShowGuide(){
        using(var dialog=new Form{Text="QuietMouse Help",Icon=Icon,StartPosition=FormStartPosition.CenterParent,
            Size=new Size(Math.Min(Width,D(650)),Math.Min(Height,D(600))),BackColor=paper,Padding=new Padding(D(14))})
        using(var reader=new StreamReader(typeof(Settings).Assembly.GetManifestResourceStream("QuietMouse.Guide"))){
            var guide=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,BorderStyle=BorderStyle.None,
                BackColor=paper,ForeColor=text,Font=Font,DetectUrls=false,AccessibleName="QuietMouse user guide",Text=reader.ReadToEnd()};
            var close=new Button{Text="Close",Dock=DockStyle.Bottom,Height=D(34),BackColor=surface,ForeColor=text,
                FlatStyle=FlatStyle.Flat,DialogResult=DialogResult.Cancel};
            dialog.Controls.Add(guide);dialog.Controls.Add(close);dialog.CancelButton=close;
            dialog.ShowDialog(this);
        }
    }
    void Changed(){dirty=true;saved.Text="";UpdateWarning();canvas.Invalidate();}
    void UpdateWarning(){
        if(screens.Any(s=>s.Input==null))return;
        for(int i=1;i<screens.Length;i++){
            var a=screens[i-1];var b=screens[i];
            if(Math.Max(a.Input.Value,b.Input.Value)>=Math.Min(a.Input.Value+(decimal)a.Height,b.Input.Value+(decimal)b.Height)){
                warning.Text="Displays "+i+" and "+(i+1)+" do not overlap. "+(bridge.Checked?"Crossing snaps to the nearest corner.":"The shared edge is blocked.");
                return;
            }
        }
        warning.Text="Mint lines mark the physically aligned crossing area.";
    }
    void ShowStatus(bool apply){
        var core=FindWindow("QuietMouse.Window","QuietMouse");
        if(core==IntPtr.Zero){runtime.Text="QuietMouse is not running";return;}
        ulong id=Convert.ToUInt64(profile.Substring(7),16);
        IntPtr result;
        if(SendMessageTimeout(core,apply?0x8002:0x8003,new IntPtr(unchecked((int)id)),new IntPtr(unchecked((int)(id>>32))),2,1500,out result)==IntPtr.Zero){
            runtime.Text="No confirmation from QuietMouse";return;
        }
        int state=result.ToInt32();
        string[] names={"No confirmation from QuietMouse",apply?"Applied":"Alignment active","Aligned by Windows","Alignment paused by you",
            "Paused · unsupported display layout","Paused · mouse hook unavailable","Different display profile active"};
        int code=state&255;
        runtime.Text=code<names.Length?names[code]:names[0];
        if(apply && code==1)saved.Text="Applied";
        if((apply || !dirty) && shortcut.Checked && code>=1 && code<=5 && (state&256)==0)runtime.Text+=" · shortcut unavailable";
    }
    void SwitchLayout(string[] layout){
        if(dirty)drafts[profile]=new Draft{Tops=screens.Select(s=>s.Input.Value).ToArray(),Heights=screens.Select(s=>s.HeightInput.Value).ToArray(),Bridge=bridge.Checked,Shortcut=shortcut.Checked};
        nextLayout=layout;Close();
    }
    protected override void WndProc(ref Message m){
        base.WndProc(ref m);
        if(m.Msg==0x8006 && Visible)ShowStatus(false);
        if(m.Msg!=0x007e || !Visible || refreshQueued)return;
        refreshQueued=true;
        BeginInvoke((Action)(()=>{
            refreshQueued=false;
            try{SwitchLayout(Detect());}
            catch(Exception){runtime.Text="Display change detected · reopen settings to refresh";}
        }));
    }
    void PaintLayout(object sender,PaintEventArgs e)
    {
        float min=screens.Min(s=>(float)s.Input.Value), max=screens.Max(s=>(float)s.Input.Value+s.Height);
        if(dragging<0){
            scale=Math.Max(0.001f,Math.Min((canvas.ClientSize.Width-D(62+8*screens.Length))/(float)screens.Sum(s=>s.Width),(canvas.ClientSize.Height-D(52))/(max-min)));
            origin=(canvas.ClientSize.Height-(max-min)*scale)/2-min*scale;
        }
        float x=(canvas.ClientSize.Width-screens.Sum(s=>s.Width)*scale-D(8*(screens.Length-1)))/2;
        var g=e.Graphics;
        g.Clear(Color.FromArgb(20,39,44));
        g.SmoothingMode=SmoothingMode.AntiAlias;
        using(var dot=new SolidBrush(Color.FromArgb(56,79,82)))
            for(int dx=D(10);dx<canvas.Width;dx+=D(16))
                for(int dy=D(10);dy<canvas.Height;dy+=D(16))g.FillEllipse(dot,dx,dy,D(1.3f),D(1.3f));
        using(var guide=new Pen(Color.FromArgb(105,163,153),density)){
            guide.DashStyle=DashStyle.Dash;
            g.DrawLine(guide,0,origin,canvas.Width,origin);
        }
        for(int i=0;i<screens.Length;i++){
            var s=screens[i]; var r=new RectangleF(x,origin+(float)s.Input.Value*scale,s.Width*scale,s.Height*scale);
            s.Bounds=r; x=r.Right+D(8);
            if(i>0){
                var previous=screens[i-1].Bounds;
                float top=Math.Max(previous.Top,r.Top),bottom=Math.Min(previous.Bottom,r.Bottom);
                if(bottom>top)using(var seam=new Pen(mint,D(2)))g.DrawLine(seam,r.Left-D(4),top,r.Left-D(4),bottom);
            }
            if(r.Width<1 || r.Height<1)continue;
            bool active=i==selected;
            using(var shape=Rounded(r,Math.Min(D(7),Math.Min(r.Width,r.Height)/3)))
            using(var fill=new LinearGradientBrush(r,active?Color.FromArgb(90,175,155):Color.FromArgb(66,112,112),
                active?Color.FromArgb(32,94,91):Color.FromArgb(36,69,78),50f))
            using(var border=new Pen(active?mint:Color.FromArgb(123,158,157),active?D(2):density)){
                g.FillPath(fill,shape);
                var state=g.Save();g.SetClip(shape);
                using(var contour=new Pen(Color.FromArgb(active?58:38,193,242,224),density))
                    for(int j=0;j<8;j++){
                        float yy=r.Top+r.Height*(0.18f+j*0.105f);
                        g.DrawBezier(contour,r.Left-r.Width*.15f,yy+r.Height*.5f,r.Left+r.Width*.30f,yy+r.Height*.54f,
                            r.Left+r.Width*.56f,yy-r.Height*.32f,r.Right+r.Width*.2f,yy-r.Height*.16f);
                    }
                g.Restore(state);g.DrawPath(border,shape);
            }
            if(r.Width>D(45) && r.Height>D(36)){
                var box=Rectangle.Round(r);
                string val=s.Input.Value.ToString("+0.##;-0.##;0")+" mm";
                using(var num=new Font("Segoe UI",18,FontStyle.Bold))
                    TextRenderer.DrawText(g,(i+1).ToString(),num,new Rectangle(box.Left,box.Top,box.Width,box.Height-D(17)),Color.White,
                        TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g,val,Font,new Rectangle(box.Left,box.Top+D(28),box.Width,box.Height-D(18)),Color.White,
                    TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            }
        }
    }
    static GraphicsPath Rounded(RectangleF r,float radius){
        var path=new GraphicsPath(); float d=2*radius;
        path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
        path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);
        path.CloseFigure();return path;
    }
    void StartDrag(object sender,MouseEventArgs e)
    {
        if(e.Button!=MouseButtons.Left)return;
        dragging=Array.FindIndex(screens,s=>s.Bounds.Contains(e.Location));
        if(dragging<0)return;
        selected=dragging;screens[selected].Input.Focus();
        anchor=e.Y; start=screens[dragging].Input.Value; canvas.Capture=true; canvas.Invalidate();
    }
    void MoveDrag(object sender,MouseEventArgs e)
    {
        canvas.Cursor=dragging>=0 || screens.Any(s=>s.Bounds.Contains(e.Location))?Cursors.SizeNS:Cursors.Default;
        if(dragging<0)return;
        var input=screens[dragging].Input;
        input.Value=Math.Max(input.Minimum,Math.Min(input.Maximum,
            decimal.Round(start+(decimal)((e.Y-anchor)/scale),1)));
    }
    void Save()
    {
        string tmp=FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            var live=Detect();
            if(live[0].Split('\t')[0]!=profile){
                MessageBox.Show(this,"The connected displays changed. Your edits will be kept as a draft for the previous layout.",Title);
                SwitchLayout(live);return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FileName));
            if(File.Exists(FileName))File.Copy(FileName,tmp);
            if(!WritePrivateProfileString(profile,"BridgeEdges",bridge.Checked?"1":"0",tmp) ||
               !WritePrivateProfileString("General","PauseShortcut",shortcut.Checked?"1":"0",tmp))throw new IOException("Could not save options.");
            foreach(var s in screens)
                if(!WritePrivateProfileString(profile,s.Id,s.Input.Value.ToString(CultureInfo.InvariantCulture),tmp) ||
                   !WritePrivateProfileString(profile,s.Id+".height",s.HeightInput.Value.ToString(CultureInfo.InvariantCulture),tmp))
                    throw new IOException("Could not write the display profile.");
            WritePrivateProfileString(null,null,null,tmp);
            if(File.Exists(FileName))File.Replace(tmp,FileName,null);else File.Move(tmp,FileName);
            dirty=false;drafts.Remove(profile);
            saved.Text="Changes saved";ShowStatus(true);
        } catch(Exception ex){MessageBox.Show(this,ex.Message,"Save settings",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        finally {if(File.Exists(tmp))File.Delete(tmp);}
    }
    [STAThread] static void Main(string[] args)
    {
        using(var mutex=new System.Threading.Mutex(false,@"Local\QuietMouse_Settings")){
            if(!mutex.WaitOne(0)){
                IntPtr existing=FindWindow(null,Title);
                if(IsIconic(existing))ShowWindow(existing,9);
                SetForegroundWindow(existing);return;
            }
            SetProcessDpiAwarenessContext(new IntPtr(-4)); Application.EnableVisualStyles();
            try{
                var layout=Detect();
                while(layout!=null)using(var form=new Settings(layout)){
                    Application.Run(form);layout=form.nextLayout;
                }
            }
            catch(Exception ex){MessageBox.Show(ex.Message,Title,MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
}
