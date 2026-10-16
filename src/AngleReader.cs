using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;

public class Settings {
    public Rectangle Height, Azimuth;
    public int Fps = 60, Threshold = 0, Polarity = 0;
    public bool OneDecimal = true, Diagnostics = true;
    public SavedPosition Position1, Position2;
    public double Tolerance = 0;
    public int PulseMs = 40;
    public int LeadMs = 60;
}
public class Reading {
    public string Text, Details; public double Confidence;
    public Reading(string text, double confidence, string details="") { Text=text; Confidence=confidence;Details=details; }
    public static Reading ValidateAngle(Reading r,bool oneDecimal,bool azimuth) {
        if(r.Text==null)return r;
        decimal value;
        if(oneDecimal&&(r.Text.Length<3||r.Text[r.Text.Length-2]!='.'||r.Text.IndexOf('.')!=r.Text.Length-2))return new Reading(null,r.Confidence,r.Details+" Rejected: expected exactly one decimal place; candidate="+r.Text);
        if(azimuth&&decimal.TryParse(r.Text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)&&value>360)return new Reading(null,r.Confidence,r.Details+" Rejected: azimuth > 360; candidate="+r.Text);
        return r;
    }
}
public class Glyph {
    public char Digit; public ulong[] Bits;
    public Glyph(char digit, ulong[] bits) { Digit=digit; Bits=bits; }
}
public class Mask {
    public bool[,] Pixels; public int Width, Height;
    public Mask(int w,int h) { Width=w; Height=h; Pixels=new bool[w,h]; }
}
public class DigitOcr {
    public readonly List<Glyph> Templates=new List<Glyph>();
    static readonly byte[] Counts=MakeCounts();
    static byte[] MakeCounts() { byte[] r=new byte[256]; for(int i=1;i<256;i++)r[i]=(byte)(r[i/2]+i%2); return r; }
    public DigitOcr() {
        string[] fonts={"Arial","Segoe UI","Tahoma","Consolas","Verdana","Trebuchet MS","Bahnschrift","DIN Alternate","Arial Narrow","Calibri","Courier New"};
        foreach(string name in fonts) foreach(FontStyle style in new[]{FontStyle.Regular,FontStyle.Bold}) foreach(int size in new[]{16,18,20,24,28,32}) {
            using(Font f=new Font(name,size,style,GraphicsUnit.Pixel)) for(char c='0';c<='9';c++) {
                using(Bitmap b=new Bitmap(80,64)) { using(Graphics g=Graphics.FromImage(b)) {
                    g.Clear(Color.Black); g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(c.ToString(),f,Brushes.White,2,2,StringFormat.GenericTypographic);
                } Add(c,MakeMask(b,0,1)); }
            }
        }
    }
    public static Mask MakeMask(Bitmap bitmap,int threshold,int polarity) {
        int w=bitmap.Width,h=bitmap.Height; Mask m=new Mask(w,h); byte[] gray=new byte[w*h]; int[] hist=new int[256];
        var bd=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try {
            byte[] row=new byte[w*4];
            for(int y=0;y<h;y++) { Marshal.Copy(IntPtr.Add(bd.Scan0,y*bd.Stride),row,0,row.Length);
                for(int x=0;x<w;x++) { int i=x*4; byte v=(byte)((row[i]*29+row[i+1]*150+row[i+2]*77)>>8); gray[y*w+x]=v; hist[v]++; }
            }
        } finally { bitmap.UnlockBits(bd); }
        int cut=threshold;
        if(cut==0) {
            double sum=0,backSum=0,best=-1; int back=0; for(int i=0;i<256;i++)sum+=i*hist[i];
            for(int i=0;i<255;i++) { back+=hist[i]; backSum+=i*hist[i]; int front=w*h-back; if(back==0||front==0)continue;
                double delta=backSum/back-(sum-backSum)/front; double score=(double)back*front*delta*delta;
                if(score>best) { best=score; cut=i; }
            }
        }
        bool light=polarity==1;
        if(polarity==0) {
            int bright=0,total=0;
            for(int y=0;y<h;y++) for(int x=0;x<w;x++) if(x==0||y==0||x==w-1||y==h-1) { total++; if(gray[y*w+x]>cut)bright++; }
            light=bright*2<=total;
        }
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)m.Pixels[x,y]=light ? gray[y*w+x]>cut : gray[y*w+x]<=cut;
        return m;
    }
    public static Rectangle Bounds(Mask m,int left,int right) {
        int minX=m.Width,minY=m.Height,maxX=-1,maxY=-1;
        for(int x=left;x<right;x++)for(int y=0;y<m.Height;y++)if(m.Pixels[x,y]) { minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y); }
        return maxX<0 ? Rectangle.Empty : Rectangle.FromLTRB(minX,minY,maxX+1,maxY+1);
    }
    public static ulong[] Normalize(Mask m,Rectangle r) {
        ulong[] bits=new ulong[6];
        for(int y=0;y<24;y++)for(int x=0;x<16;x++) {
            int sx=r.X+Math.Min(r.Width-1,(int)((x+0.5)*r.Width/16));
            int sy=r.Y+Math.Min(r.Height-1,(int)((y+0.5)*r.Height/24));
            if(m.Pixels[sx,sy]) { int i=y*16+x; bits[i/64]|=1UL<<(i%64); }
        }
        return bits;
    }
    public void Add(char c,Mask m) { Rectangle b=Bounds(m,0,m.Width); if(!b.IsEmpty)Templates.Add(new Glyph(c,Normalize(m,b))); }
    public static List<Rectangle> Segments(Mask m) {
        List<Rectangle> r=new List<Rectangle>(); int start=-1;
        for(int x=0;x<=m.Width;x++) {
            bool ink=false; if(x<m.Width)for(int y=0;y<m.Height;y++)if(m.Pixels[x,y]){ink=true;break;}
            if(ink&&start<0)start=x;
            if(!ink&&start>=0){r.Add(Bounds(m,start,x));start=-1;}
        }
        return r;
    }
    public static Mask CleanSymbols(Mask original) {
        // Remove tiny detached noise and upper degree symbols, even if their
        // horizontal projection overlaps the last digit. Preserve bottom dots.
        var projected=Segments(original);int height=0,bottom=0;
        foreach(var r in projected)if(r.Height>height){height=r.Height;bottom=r.Bottom;}
        if(height<5)return original;
        Mask clean=new Mask(original.Width,original.Height);bool[,] seen=new bool[original.Width,original.Height];
        Queue<Point> queue=new Queue<Point>();List<Point> points=new List<Point>();
        for(int y=0;y<original.Height;y++)for(int x=0;x<original.Width;x++) {
            if(seen[x,y]||!original.Pixels[x,y])continue;
            seen[x,y]=true;queue.Enqueue(new Point(x,y));points.Clear();int top=y,low=y;
            while(queue.Count>0) {Point p=queue.Dequeue();points.Add(p);top=Math.Min(top,p.Y);low=Math.Max(low,p.Y);
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++){int xx=p.X+dx,yy=p.Y+dy;if(xx>=0&&yy>=0&&xx<original.Width&&yy<original.Height&&!seen[xx,yy]&&original.Pixels[xx,yy]){seen[xx,yy]=true;queue.Enqueue(new Point(xx,yy));}}
            }
            bool upper=(low-top+1)<height*0.55&&low+1<bottom-height*0.4;
            if(points.Count<=1||upper)continue;
            foreach(Point p in points)clean.Pixels[p.X,p.Y]=true;
        }
        return clean;
    }
    static int Distance(ulong[] a,ulong[] b) {
        int n=0;for(int i=0;i<a.Length;i++){ulong v=a[i]^b[i];for(int j=0;j<8;j++){n+=Counts[(byte)v];v>>=8;}}return n;
    }
    public Reading Read(Bitmap bitmap,int threshold,int polarity) {
        Reading first=ReadMask(CleanSymbols(MakeMask(bitmap,threshold,polarity)));
        if(first.Text!=null||threshold!=0)return first;
        Reading low=ReadMask(CleanSymbols(MakeMask(bitmap,140,polarity)));
        Reading high=ReadMask(CleanSymbols(MakeMask(bitmap,200,polarity)));
        // A fallback is accepted only when two different thresholds agree.
        if(low.Text!=null&&low.Text==high.Text)return new Reading(low.Text,Math.Min(low.Confidence,high.Confidence),"Recovered with thresholds 140 and 200. "+low.Details);
        first.Details+=" Fallback140="+(low.Text??"?")+" Fallback200="+(high.Text??"?");return first;
    }
    Reading ReadMask(Mask m) {
        var rects=Segments(m);StringBuilder trace=new StringBuilder();
        if(rects.Count==0||rects.Count>12)return new Reading(null,0,"Segments="+rects.Count);
        int maxHeight=0,bottom=0; foreach(var r in rects)if(r.Height>maxHeight){maxHeight=r.Height;bottom=r.Bottom;}
        if(maxHeight<5)return new Reading(null,0,"Glyph height < 5");
        StringBuilder text=new StringBuilder(); double confidence=1; bool dot=false;
        foreach(Rectangle r in rects) {
            if(r.Height<maxHeight*0.55) {
                if(r.Bottom>=bottom-maxHeight*0.25 && r.Width<=maxHeight*0.4 && text.Length>0 && !dot) { text.Append('.');dot=true; }
                else if(r.Bottom<bottom-maxHeight*0.4)continue;
                else return new Reading(null,0,"Unclassified small symbol "+r);
                continue;
            }
            if(r.Width>maxHeight*1.25)return new Reading(null,0,"Wide or merged glyph "+r);
            ulong[] bits=Normalize(m,r); int best=385,second=385;char digit='?';
            int[] byDigit=new int[10];for(int i=0;i<10;i++)byDigit[i]=385;
            foreach(Glyph t in Templates) { int d=Distance(bits,t.Bits);int i=t.Digit-'0';if(d<byDigit[i])byDigit[i]=d; }
            for(int i=0;i<10;i++)if(byDigit[i]<best){second=best;best=byDigit[i];digit=(char)('0'+i);}else if(byDigit[i]<second)second=byDigit[i];
            double score=1-best/384.0;
            trace.AppendFormat(CultureInfo.InvariantCulture,"[{0} candidate={1} score={2:F3} margin={3}] ",r,digit,score,second-best);
            if(score<0.73||second-best<5)return new Reading(null,score,trace+"Rejected: score or ambiguity");
            confidence=Math.Min(confidence,score);text.Append(digit);
        }
        decimal value;
        if(text.Length==0||text[text.Length-1]=='.'||!decimal.TryParse(text.ToString(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value))return new Reading(null,0,trace+"Invalid numeric format");
        return new Reading(text.ToString(),confidence,trace.ToString());
    }
}
public class DiagnosticSample : IDisposable {
    public Bitmap Image;public string Name,Info;public int Threshold,Polarity;
    public void Dispose(){Image.Dispose();}
}
public class DiagnosticWriter : IDisposable {
    readonly BlockingCollection<DiagnosticSample> queue=new BlockingCollection<DiagnosticSample>(8);readonly Thread thread;readonly string directory;
    public DiagnosticWriter(string root){directory=Path.Combine(root,"diagnostics-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));thread=new Thread(Work){IsBackground=true};thread.Start();}
    public void Offer(Bitmap image,string name,string info,int threshold,int polarity){var sample=new DiagnosticSample{Image=(Bitmap)image.Clone(),Name=name,Info=info,Threshold=threshold,Polarity=polarity};if(!queue.TryAdd(sample))sample.Dispose();}
    public static void SaveMask(Bitmap image,string path,int threshold,int polarity){Mask m=DigitOcr.CleanSymbols(DigitOcr.MakeMask(image,threshold,polarity));using(Bitmap b=new Bitmap(m.Width,m.Height)){for(int y=0;y<m.Height;y++)for(int x=0;x<m.Width;x++)b.SetPixel(x,y,m.Pixels[x,y]?Color.White:Color.Black);b.Save(path,ImageFormat.Png);}}
    void Work(){foreach(var sample in queue.GetConsumingEnumerable())using(sample){try{Directory.CreateDirectory(directory);string path=Path.Combine(directory,sample.Name);sample.Image.Save(path+".png",ImageFormat.Png);SaveMask(sample.Image,path+"-mask.png",sample.Threshold,sample.Polarity);File.WriteAllText(path+".txt",sample.Info,Encoding.UTF8);}catch{/* Diagnostics must not stop live capture. */}}}
    public void Dispose(){queue.CompleteAdding();thread.Join();queue.Dispose();}
}
public class Capture : IDisposable {
    [DllImport("gdi32.dll",SetLastError=true)]static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
    [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr window,IntPtr dc);
    IntPtr source; public Bitmap Bitmap; Graphics graphics;
    public Capture(Rectangle r) { source=GetDC(IntPtr.Zero);Bitmap=new Bitmap(r.Width,r.Height,PixelFormat.Format32bppRgb);graphics=Graphics.FromImage(Bitmap); }
    public void Grab(Rectangle r) { IntPtr dc=graphics.GetHdc();try{if(!BitBlt(dc,0,0,r.Width,r.Height,source,r.X,r.Y,0x00CC0020)){ReleaseDC(IntPtr.Zero,source);source=GetDC(IntPtr.Zero);if(!BitBlt(dc,0,0,r.Width,r.Height,source,r.X,r.Y,0x00CC0020))throw new InvalidOperationException("Не удалось захватить экран. Код Windows: "+Marshal.GetLastWin32Error()+". Проверьте, что рабочий стол доступен и сеанс не заблокирован.");}}finally{graphics.ReleaseHdc(dc);} }
    public void Dispose(){graphics.Dispose();Bitmap.Dispose();ReleaseDC(IntPtr.Zero,source);}
}
public class Selector : Form {
    Bitmap screen; Point start,end; bool dragging; public Rectangle Selected;
    public Selector(string title) {
        Rectangle v=SystemInformation.VirtualScreen;
        using(Capture c=new Capture(v)){c.Grab(v);screen=(Bitmap)c.Bitmap.Clone();}
        FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Bounds=v;TopMost=true;DoubleBuffered=true;Cursor=Cursors.Cross;KeyPreview=true;Text=title;
        KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){DialogResult=DialogResult.Cancel;Close();}};
        MouseDown+=(s,e)=>{if(e.Button!=MouseButtons.Left)return;start=end=e.Location;dragging=true;Capture=true;};
        MouseMove+=(s,e)=>{if(dragging){end=e.Location;Invalidate();}};
        MouseUp+=(s,e)=>{if(!dragging)return;end=e.Location;Rectangle r=LocalRect();if(r.Width>=5&&r.Height>=5){Selected=new Rectangle(r.X+v.X,r.Y+v.Y,r.Width,r.Height);DialogResult=DialogResult.OK;Close();}else{dragging=false;Invalidate();}};
    }
    Rectangle LocalRect(){return Rectangle.FromLTRB(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y),Math.Max(start.X,end.X),Math.Max(start.Y,end.Y));}
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.DrawImageUnscaled(screen,0,0);using(Brush dim=new SolidBrush(Color.FromArgb(110,0,0,0)))e.Graphics.FillRectangle(dim,ClientRectangle);
        if(dragging){Rectangle r=LocalRect();if(r.Width>0&&r.Height>0)e.Graphics.DrawImage(screen,r,r,GraphicsUnit.Pixel);using(Pen p=new Pen(Color.Lime,2))e.Graphics.DrawRectangle(p,r);}
        using(Font f=new Font("Segoe UI",15,FontStyle.Bold)) { e.Graphics.FillRectangle(Brushes.Black,20,20,850,60);e.Graphics.DrawString(Text+" — обведите число мышью. Esc — отмена",f,Brushes.White,28,30); }
    }
    protected override void Dispose(bool disposing){if(disposing&&screen!=null)screen.Dispose();base.Dispose(disposing);}
}
public class Packet : IDisposable {
    public Bitmap[] Images; public Reading[] Values;public double Fps,Ms;public long Frames,Late;public string Error;
    public void Dispose(){if(Images!=null)foreach(var b in Images)if(b!=null)b.Dispose();}
}
public partial class MainForm : Form {
    readonly string data=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data");
    Settings settings=new Settings();DigitOcr ocr;Thread worker;volatile bool stopping;
    object sync=new object(); Packet pending;
    Label[] values=new Label[2], coords=new Label[2]; PictureBox[] previews=new PictureBox[2];
    List<Control> locked=new List<Control>();Button run;Label status;NumericUpDown fps,threshold;ComboBox polarity;CheckBox log,oneDecimal,diagnostics; System.Windows.Forms.Timer ui;
    Reading[] lastReadings;long lastFrame;
    public MainForm() {
        Text="Высота и азимут — v5, точное совпадение, ручной ввод, F6/F7";ClientSize=new Size(1100,625);Font=new Font("Segoe UI",10);FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;
        try{Directory.CreateDirectory(data);if(File.Exists(Path.Combine(data,"settings.xml")))using(var f=File.OpenRead(Path.Combine(data,"settings.xml")))settings=(Settings)new XmlSerializer(typeof(Settings)).Deserialize(f);}catch{settings=new Settings();}
        Label intro=new Label{Text="Выберите две области, плотно охватывающие только числа.",Left=18,Top=15,Width=690,Height=26};Controls.Add(intro);
        for(int i=0;i<2;i++) {
            int index=i,y=50+i*160;GroupBox group=new GroupBox{Text=(i==0?"1. Высота":"2. Азимут"),Left=18,Top=y,Width=690,Height=150};Controls.Add(group);
            Button choose=new Button{Text="Выбрать область",Left=12,Top=25,Width=155,Height=32};choose.Click+=(s,e)=>SelectRegion(index);group.Controls.Add(choose);locked.Add(choose);
            Button manual=new Button{Text="Координаты…",Left=12,Top=63,Width=155,Height=30};manual.Click+=(s,e)=>Manual(index);group.Controls.Add(manual);locked.Add(manual);
            coords[i]=new Label{Left=12,Top=100,Width=295,Height=43};group.Controls.Add(coords[i]);
            previews[i]=new PictureBox{Left=180,Top=28,Width=260,Height=62,BackColor=Color.FromArgb(30,30,35),SizeMode=PictureBoxSizeMode.Zoom};group.Controls.Add(previews[i]);
            values[i]=new Label{Text="—",Left=450,Top=28,Width=225,Height=64,Font=new Font("Segoe UI",25,FontStyle.Bold)};group.Controls.Add(values[i]);
            Button train=new Button{Text="Обучить цифры…",Left=325,Top=105,Width=160,Height=28};train.Click+=(s,e)=>Train(index);group.Controls.Add(train);locked.Add(train);
        }
        Controls.Add(new Label{Text="Кадров/с:",Left=18,Top=383,Width=83});fps=new NumericUpDown{Left=102,Top=380,Width=65,Minimum=1,Maximum=240,Value=Math.Max(1,Math.Min(240,settings.Fps))};Controls.Add(fps);locked.Add(fps);
        Controls.Add(new Label{Text="Цифры:",Left=188,Top=383,Width=65});polarity=new ComboBox{Left=255,Top=380,Width=140,DropDownStyle=ComboBoxStyle.DropDownList};polarity.Items.AddRange(new object[]{"Авто","Светлые","Тёмные"});polarity.SelectedIndex=Math.Max(0,Math.Min(2,settings.Polarity));Controls.Add(polarity);locked.Add(polarity);
        Controls.Add(new Label{Text="Порог (0 = авто):",Left=420,Top=383,Width=155});threshold=new NumericUpDown{Left=580,Top=380,Width=90,Minimum=0,Maximum=255,Value=Math.Max(0,Math.Min(255,settings.Threshold))};Controls.Add(threshold);locked.Add(threshold);
        log=new CheckBox{Text="Сохранять изменения в CSV (папка data)",Left=18,Top=421,Width=420,Checked=true};Controls.Add(log);locked.Add(log);
        oneDecimal=new CheckBox{Text="Один знак после точки",Left=445,Top=421,Width=270,Checked=settings.OneDecimal};Controls.Add(oneDecimal);locked.Add(oneDecimal);
        diagnostics=new CheckBox{Text="Сохранять снимки ошибок (до 20 на каждую область)",Left=18,Top=450,Width=690,Checked=settings.Diagnostics};Controls.Add(diagnostics);locked.Add(diagnostics);
        run=new Button{Text="Запустить",Left=18,Top=487,Width=150,Height=40,Enabled=false};run.Click+=(s,e)=>{if(worker!=null)Stop();else Start();};Controls.Add(run);
        Button folder=new Button{Text="Открыть записи",Left=185,Top=487,Width=170,Height=40};folder.Click+=(s,e)=>Process.Start("explorer.exe",data);Controls.Add(folder);
        Button snapshot=new Button{Text="Сохранить пример",Left=375,Top=487,Width=190,Height=40};snapshot.Click+=(s,e)=>SaveExample();Controls.Add(snapshot);
        status=new Label{Text="Подготовка распознавания…",Left=18,Top=542,Width=690,Height=75};Controls.Add(status);
        BuildSteering();UpdateCoords();ui=new System.Windows.Forms.Timer{Interval=50};ui.Tick+=(s,e)=>{Display();UpdateSteeringUi();};ui.Start();
        Shown+=(s,e)=>{ThreadPool.QueueUserWorkItem(_=>{try{var engine=new DigitOcr();LoadTemplates(engine);BeginInvoke((Action)(()=>{ocr=engine;run.Enabled=true;status.Text="Готово. Выберите области и нажмите «Запустить».";}));}catch(Exception ex){if(!IsDisposed)BeginInvoke((Action)(()=>status.Text=ex.Message));}});};
        FormClosing+=(s,e)=>{Stop();ui.Stop();lock(sync){if(pending!=null){pending.Dispose();pending=null;}}};
    }
    void LoadTemplates(DigitOcr engine) {
        foreach(string f in Directory.GetFiles(data,"digit-*.png")){string n=Path.GetFileName(f);if(n.Length>6&&n[6]>='0'&&n[6]<='9')using(Bitmap b=new Bitmap(f))engine.Add(n[6],DigitOcr.MakeMask(b,128,1));}
    }
    Rectangle RegionAt(int i){return i==0?settings.Height:settings.Azimuth;}
    void SetRegion(int i,Rectangle r){if(i==0)settings.Height=r;else settings.Azimuth=r;Save();UpdateCoords();}
    void Save(){settings.Tolerance=0;settings.Fps=(int)fps.Value;settings.Polarity=polarity.SelectedIndex;settings.Threshold=(int)threshold.Value;settings.OneDecimal=oneDecimal.Checked;settings.Diagnostics=diagnostics.Checked;using(var f=File.Create(Path.Combine(data,"settings.xml")))new XmlSerializer(typeof(Settings)).Serialize(f,settings);}
    void SaveExample(){
        if(lastReadings==null||previews[0].Image==null||previews[1].Image==null){MessageBox.Show("Сначала запустите захват.");return;}
        try{string folder=Path.Combine(data,"example-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));Directory.CreateDirectory(folder);
            StringBuilder info=new StringBuilder("Displayed frame: "+lastFrame+Environment.NewLine);
            for(int i=0;i<2;i++)using(Bitmap image=new Bitmap(previews[i].Image)){string name=i==0?"height":"azimuth";image.Save(Path.Combine(folder,name+".png"),ImageFormat.Png);DiagnosticWriter.SaveMask(image,Path.Combine(folder,name+"-mask.png"),settings.Threshold,settings.Polarity);info.AppendLine(name+" region="+RegionAt(i)+" result="+(lastReadings[i].Text??"?")+" "+lastReadings[i].Details);}
            File.WriteAllText(Path.Combine(folder,"details.txt"),info.ToString(),Encoding.UTF8);ZipFile.CreateFromDirectory(folder,folder+".zip");MessageBox.Show("Сохранён архив с двумя областями:\n"+folder+".zip");
        }catch(Exception ex){MessageBox.Show("Не удалось сохранить пример: "+ex.Message);}
    }
    void UpdateCoords(){for(int i=0;i<2;i++){var r=RegionAt(i);coords[i].Text=r.Width>0?String.Format("X {0}, Y {1} • {2} × {3}",r.X,r.Y,r.Width,r.Height):"Область не выбрана";}}
    void SelectRegion(int i){Hide();try{Thread.Sleep(180);using(var picker=new Selector(i==0?"Высота":"Азимут")){if(picker.ShowDialog()==DialogResult.OK)SetRegion(i,picker.Selected);}}catch(Exception ex){MessageBox.Show(ex.Message);}finally{Show();Activate();}}
    void Manual(int i){Rectangle r=RegionAt(i);using(Form d=new Form{Text="Координаты области",ClientSize=new Size(320,235),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
        string[] names={"X","Y","Ширина","Высота"};int[] nums={r.X,r.Y,Math.Max(5,r.Width),Math.Max(5,r.Height)};NumericUpDown[] boxes=new NumericUpDown[4];
        for(int j=0;j<4;j++){d.Controls.Add(new Label{Text=names[j],Left=16,Top=20+j*40,Width=110});boxes[j]=new NumericUpDown{Left=130,Top=16+j*40,Width=160,Minimum=j<2?-100000:5,Maximum=100000,Value=nums[j]};d.Controls.Add(boxes[j]);}
        Button ok=new Button{Text="Сохранить",Left=130,Top=183,Width=160,DialogResult=DialogResult.OK};d.Controls.Add(ok);d.AcceptButton=ok;
        if(d.ShowDialog(this)==DialogResult.OK)SetRegion(i,new Rectangle((int)boxes[0].Value,(int)boxes[1].Value,(int)boxes[2].Value,(int)boxes[3].Value));
    }}
    void Train(int index) {
        Rectangle r=RegionAt(index);if(r.Width<5){MessageBox.Show("Сначала выберите область.");return;}if(ocr==null)return;
        Hide();Bitmap b=null;try{Thread.Sleep(180);using(Capture c=new Capture(r)){c.Grab(r);b=(Bitmap)c.Bitmap.Clone();}}catch(Exception ex){MessageBox.Show(ex.Message);}finally{Show();Activate();}if(b==null)return;
        using(b){Mask m=DigitOcr.CleanSymbols(DigitOcr.MakeMask(b,(int)threshold.Value,polarity.SelectedIndex));var parts=DigitOcr.Segments(m);int max=0;foreach(var p in parts)max=Math.Max(max,p.Height);
        List<Rectangle> digits=new List<Rectangle>();foreach(var p in parts)if(p.Height>=max*0.55&&p.Height>=5)digits.Add(p);
        if(digits.Count==0){MessageBox.Show("Цифры не выделены. Попробуйте поменять «Цифры» и «Порог».");return;}
        using(Form d=new Form{Text="Обучение на снимке",ClientSize=new Size(540,230),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}) {
            d.Controls.Add(new PictureBox{Image=b,Left=20,Top=15,Width=500,Height=80,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.Black});
            d.Controls.Add(new Label{Text="Введите число со снимка. Точку и ° можно оставить.\nНужно цифр: "+digits.Count,Left=20,Top=105,Width=500,Height=45});
            TextBox input=new TextBox{Left=20,Top=160,Width=280};d.Controls.Add(input);Button ok=new Button{Text="Обучить",Left=330,Top=158,Width=170};d.Controls.Add(ok);d.AcceptButton=ok;
            ok.Click+=(s,e)=>{string t=input.Text.Trim().Replace(".","").Replace(",","").Replace("°","").Replace(" ","");if(t.Length!=digits.Count){MessageBox.Show("Количество цифр не совпадает.");return;}foreach(char ch in t)if(ch<'0'||ch>'9'){MessageBox.Show("Введите только цифры 0–9.");return;}
                for(int j=0;j<t.Length;j++){Rectangle p=digits[j];Mask one=new Mask(p.Width,p.Height);using(Bitmap sample=new Bitmap(p.Width,p.Height)){for(int y=0;y<p.Height;y++)for(int x=0;x<p.Width;x++){bool ink=m.Pixels[p.X+x,p.Y+y];one.Pixels[x,y]=ink;sample.SetPixel(x,y,ink?Color.White:Color.Black);}sample.Save(Path.Combine(data,"digit-"+t[j]+"-"+Guid.NewGuid().ToString("N")+".png"),ImageFormat.Png);}ocr.Add(t[j],one);}
                d.DialogResult=DialogResult.OK;d.Close();};d.ShowDialog(this);
        }}
    }
    void Start(){
        for(int i=0;i<2;i++){Rectangle r=RegionAt(i);if(r.Width<5||r.Height<5||!SystemInformation.VirtualScreen.Contains(r)||r.Width>2000||r.Height>1000){MessageBox.Show("Выберите небольшую область с числом внутри экрана: "+(i==0?"Высота":"Азимут"));return;}}
        try{Save();}catch(Exception ex){MessageBox.Show("Не удалось сохранить настройки: "+ex.Message);return;}
        lock(sync){if(pending!=null){pending.Dispose();pending=null;}}for(int i=0;i<2;i++)values[i].Text="—";
        foreach(Control c in locked)c.Enabled=false;run.Text="Остановить";status.Text="Захват запущен…";stopping=false;bool record=log.Checked;worker=new Thread(()=>Loop(record)){IsBackground=true,Name="Screen capture and OCR"};worker.Start();
    }
    void Stop(){stopping=true;CancelNavigation();liveAngle=null;if(worker!=null){worker.Join();worker=null;status.Text="Остановлено. Запись завершена.";}foreach(Control c in locked)c.Enabled=true;run.Text="Запустить";}
    void Publish(Packet p){lock(sync){if(pending!=null)pending.Dispose();pending=p;}}
    void Loop(bool record) {
        StreamWriter csv=null;bool timer=false;
        try{
            timer=Native.timeBeginPeriod(1)==0;Rectangle a=settings.Height,b=settings.Azimuth;
            if(record){csv=new StreamWriter(Path.Combine(data,"readings-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".csv"),false,new UTF8Encoding(true));csv.WriteLine("time;elapsed_ms;frame;height;azimuth;height_confidence;azimuth_confidence");}
            Rectangle combined=Rectangle.Union(a,b);
            using(DiagnosticWriter diagnosticWriter=settings.Diagnostics?new DiagnosticWriter(data):null)
            using(Capture capture=new Capture(combined))using(Bitmap heightImage=new Bitmap(a.Width,a.Height,PixelFormat.Format32bppArgb))using(Bitmap azimuthImage=new Bitmap(b.Width,b.Height,PixelFormat.Format32bppArgb))using(Graphics gh=Graphics.FromImage(heightImage))using(Graphics ga=Graphics.FromImage(azimuthImage)) {
                Rectangle relativeA=new Rectangle(a.X-combined.X,a.Y-combined.Y,a.Width,a.Height),relativeB=new Rectangle(b.X-combined.X,b.Y-combined.Y,b.Width,b.Height);
                Stopwatch watch=Stopwatch.StartNew();double period=1000.0/settings.Fps,next=0,update=0,report=0,lastFlush=0;long count=0,late=0,lastCount=0;double actual=0;string previous=null;
                double[] lastDiagnostic={-1000,-1000};int[] diagnosticCount={0,0};
                while(!stopping) {
                    double delay=next-watch.Elapsed.TotalMilliseconds;if(delay>1){Thread.Sleep((int)(delay-0.5));continue;}
                    if(delay>0){Thread.Yield();continue;}
                    double at=watch.Elapsed.TotalMilliseconds;DateTime timestamp=DateTime.Now;capture.Grab(combined);
                    gh.DrawImage(capture.Bitmap,new Rectangle(Point.Empty,a.Size),relativeA,GraphicsUnit.Pixel);ga.DrawImage(capture.Bitmap,new Rectangle(Point.Empty,b.Size),relativeB,GraphicsUnit.Pixel);count++;
                    Reading ra=Reading.ValidateAngle(ocr.Read(heightImage,settings.Threshold,settings.Polarity),settings.OneDecimal,false),rb=Reading.ValidateAngle(ocr.Read(azimuthImage,settings.Threshold,settings.Polarity),settings.OneDecimal,true);
                    SetLiveAngle(ra,rb,count);
                    if(diagnosticWriter!=null){Reading[] readings={ra,rb};Bitmap[] images={heightImage,azimuthImage};for(int i=0;i<2;i++)if((readings[i].Text==null||count==1)&&at-lastDiagnostic[i]>=1000&&diagnosticCount[i]<20){diagnosticWriter.Offer(images[i],(i==0?"height":"azimuth")+"-frame-"+count,timestamp.ToString("O")+" frame="+count+" region="+(i==0?a:b)+" threshold="+settings.Threshold+" polarity="+settings.Polarity+" result="+(readings[i].Text??"?")+Environment.NewLine+readings[i].Details,settings.Threshold,settings.Polarity);lastDiagnostic[i]=at;diagnosticCount[i]++;}}
                    double ms=watch.Elapsed.TotalMilliseconds-at;string key=(ra.Text??"?")+";"+(rb.Text??"?");
                    if(csv!=null&&key!=previous){csv.WriteLine(String.Join(";",timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),at.ToString("F3",CultureInfo.InvariantCulture),count.ToString(),ra.Text??"?",rb.Text??"?",ra.Confidence.ToString("F3",CultureInfo.InvariantCulture),rb.Confidence.ToString("F3",CultureInfo.InvariantCulture)));previous=key;}
                    double now=watch.Elapsed.TotalMilliseconds;
                    if(csv!=null&&now-lastFlush>=1000){csv.Flush();lastFlush=now;}
                    if(now-report>=500){actual=(count-lastCount)*1000.0/(now-report);report=now;lastCount=count;}
                    next+=period;if(now>next){long missed=(long)((now-next)/period)+1;late+=missed;next+=missed*period;}
                    if(now>=update){Publish(new Packet{Images=new[]{(Bitmap)heightImage.Clone(),(Bitmap)azimuthImage.Clone()},Values=new[]{ra,rb},Frames=count,Late=late,Fps=actual,Ms=ms});update=now+100;}
                }
            }
        }catch(Exception ex){Publish(new Packet{Error=ex.Message});}finally{if(csv!=null)csv.Dispose();if(timer)Native.timeEndPeriod(1);}
    }
    void Display(){Packet p;lock(sync){p=pending;pending=null;}if(p==null)return;
        using(p){if(p.Error!=null){Stop();status.Text="Ошибка: "+p.Error;return;}
        lastReadings=p.Values;lastFrame=p.Frames;
        for(int i=0;i<2;i++){var old=previews[i].Image;previews[i].Image=p.Images[i];p.Images[i]=null;if(old!=null)old.Dispose();values[i].Text=p.Values[i].Text??"—";values[i].ForeColor=p.Values[i].Text==null?Color.DarkOrange:Color.DarkGreen;}
        status.Text=String.Format("{0:F1} кадров/с из {1} • захват + распознавание: {2:F1} мс\nКадров: {3} • пропущено тактов: {4}. «—» = число не распознано.",p.Fps,settings.Fps,p.Ms,p.Frames,p.Late)+(worker==null?" Остановлено.":"");}
    }
}
static class Native {
    [DllImport("user32.dll")]public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")]public static extern bool SetProcessDPIAware();
    [DllImport("winmm.dll")]public static extern uint timeBeginPeriod(uint value);
    [DllImport("winmm.dll")]public static extern uint timeEndPeriod(uint value);
}
static class Program {
    [STAThread]static int Main(string[] args) {
        try{Native.SetProcessDpiAwarenessContext(new IntPtr(-4));}catch{Native.SetProcessDPIAware();}
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>0&&args[0]=="--self-test")return SelfTest(args.Length>1?args[1]:"self-test.txt",args.Length>2&&args[2]=="--capture");
        if(args.Length>1&&args[0]=="--preview") {using(MainForm form=new MainForm()){form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();using(Bitmap bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(args[1],ImageFormat.Png);}form.Close();}return 0;}
        try{Application.Run(new MainForm());return 0;}catch(Exception ex){MessageBox.Show(ex.ToString(),"Ошибка");return 1;}
    }
    static int SelfTest(string output,bool captureTest) {
        List<string> lines=new List<string>();int failures=0;Stopwatch sw=Stopwatch.StartNew();DigitOcr ocr=new DigitOcr();lines.Add("Templates: "+ocr.Templates.Count+"; initialization ms: "+sw.ElapsedMilliseconds);
        foreach(string font in new[]{"Arial","Segoe UI","Consolas"})foreach(string text in new[]{"123","045","360","27.5"})foreach(bool light in new[]{true,false}) {
            using(Bitmap b=new Bitmap(180,56))using(Graphics g=Graphics.FromImage(b))using(Font f=new Font(font,32,FontStyle.Regular,GraphicsUnit.Pixel)) {
                g.Clear(light?Color.Black:Color.White);g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.DrawString(text,f,light?Brushes.White:Brushes.Black,5,5,StringFormat.GenericTypographic);
                var r=ocr.Read(b,0,0);bool ok=r.Text==text;if(!ok)failures++;lines.Add((ok?"PASS ":"FAIL ")+font+" "+text+" "+(light?"light":"dark")+" -> "+(r.Text??"?")+" confidence "+r.Confidence.ToString("F3",CultureInfo.InvariantCulture));
            }
        }
        using(Bitmap blank=new Bitmap(100,40)){var r=ocr.Read(blank,0,0);if(r.Text!=null)failures++;lines.Add("Blank rejected: "+(r.Text==null));}
        foreach(int size in new[]{16,18,24})using(Bitmap b=new Bitmap(200,50))using(Graphics g=Graphics.FromImage(b))using(Font f=new Font("Arial",size,FontStyle.Bold,GraphicsUnit.Pixel)){
            g.Clear(Color.Black);g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.DrawString("0123456789",f,Brushes.White,4,12,StringFormat.GenericTypographic);var r=ocr.Read(b,0,1);bool ok=r.Text=="0123456789";if(!ok)failures++;lines.Add("Small digits "+size+": "+ok+" result="+(r.Text??"?"));
        }
        using(Bitmap b=new Bitmap(180,70))using(Graphics g=Graphics.FromImage(b))using(Font f=new Font("Arial",32,FontStyle.Regular,GraphicsUnit.Pixel)){
            g.Clear(Color.Black);g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.DrawString("27.5",f,Brushes.White,5,20,StringFormat.GenericTypographic);var initial=DigitOcr.MakeMask(b,0,1);var parts=DigitOcr.Segments(initial);Rectangle last=parts[parts.Count-1];g.DrawEllipse(Pens.White,last.X+2,last.Y-8,5,5);b.SetPixel(170,2,Color.White);
            var r=ocr.Read(b,0,1);bool ok=r.Text=="27.5";if(!ok)failures++;lines.Add("Detached degree overlapping digit column and isolated noise: "+ok+" result="+(r.Text??"?"));
        }
        bool formatOk=Reading.ValidateAngle(new Reading("8",0.9),true,true).Text==null&&Reading.ValidateAngle(new Reading("255.3",0.9),true,true).Text=="255.3"&&Reading.ValidateAngle(new Reading("360.1",0.9),true,true).Text==null&&Reading.ValidateAngle(new Reading("8",0.9),false,true).Text=="8";
        if(!formatOk)failures++;lines.Add("Angle format and azimuth range validation: "+formatOk);
        string diagnosticTest=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)),"diagnostic-test-"+Guid.NewGuid().ToString("N"));
        using(DiagnosticWriter writer=new DiagnosticWriter(diagnosticTest))using(Bitmap b=new Bitmap(60,30)){writer.Offer(b,"test-frame-1","test diagnostic metadata",0,1);}
        bool diagnosticOk=Directory.GetFiles(diagnosticTest,"*.png",SearchOption.AllDirectories).Length==2&&Directory.GetFiles(diagnosticTest,"*.txt",SearchOption.AllDirectories).Length==1;
        if(!diagnosticOk)failures++;lines.Add("Background diagnostic writer saves raw crop, mask and metadata: "+diagnosticOk);
        using(Bitmap b=new Bitmap(180,56))using(Graphics g=Graphics.FromImage(b))using(Font f=new Font("Arial",32,FontStyle.Regular,GraphicsUnit.Pixel)){
            g.Clear(Color.Black);g.DrawString("123",f,Brushes.White,5,5,StringFormat.GenericTypographic);sw.Restart();for(int i=0;i<200;i++)ocr.Read(b,0,0);lines.Add("Synthetic OCR average ms: "+(sw.Elapsed.TotalMilliseconds/200).ToString("F3",CultureInfo.InvariantCulture));
        }
        Settings config=new Settings{Height=new Rectangle(-120,45,150,50),Azimuth=new Rectangle(123,456,100,40),Position1=new SavedPosition(12.3,359.9),Position2=new SavedPosition(45.6,0.1),Tolerance=0.3,PulseMs=60};
        using(MemoryStream stream=new MemoryStream()){var serializer=new XmlSerializer(typeof(Settings));serializer.Serialize(stream,config);stream.Position=0;var restored=(Settings)serializer.Deserialize(stream);bool ok=restored.Height==config.Height&&restored.Azimuth==config.Azimuth&&restored.Fps==60&&restored.Position1!=null&&restored.Position2!=null&&restored.Position1.Height==12.3&&restored.Position1.Azimuth==359.9&&restored.Position2.Height==45.6&&restored.Position2.Azimuth==0.1&&restored.Tolerance==0.3&&restored.PulseMs==60;if(!ok)failures++;lines.Add("Settings round trip: "+ok);}
        using(Bitmap source=new Bitmap(300,56,PixelFormat.Format32bppRgb))using(Bitmap first=new Bitmap(150,56))using(Bitmap second=new Bitmap(150,56))using(Graphics g=Graphics.FromImage(source))using(Graphics g1=Graphics.FromImage(first))using(Graphics g2=Graphics.FromImage(second))using(Font f=new Font("Arial",32,FontStyle.Regular,GraphicsUnit.Pixel)) {
            g.Clear(Color.Black);g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.DrawString("27.5",f,Brushes.White,5,5,StringFormat.GenericTypographic);g.DrawString("360",f,Brushes.White,155,5,StringFormat.GenericTypographic);
            g1.DrawImage(source,new Rectangle(0,0,150,56),new Rectangle(0,0,150,56),GraphicsUnit.Pixel);g2.DrawImage(source,new Rectangle(0,0,150,56),new Rectangle(150,0,150,56),GraphicsUnit.Pixel);
            bool ok=ocr.Read(first,0,0).Text=="27.5"&&ocr.Read(second,0,0).Text=="360";if(!ok)failures++;lines.Add("Shared RGB frame splitting and OCR: "+ok);
        }
        if(captureTest)try {Rectangle screen=SystemInformation.VirtualScreen;Rectangle area=new Rectangle(screen.X,screen.Y,300,50);using(Capture capture=new Capture(area))using(Bitmap first=new Bitmap(150,50))using(Bitmap second=new Bitmap(150,50))using(Graphics g1=Graphics.FromImage(first))using(Graphics g2=Graphics.FromImage(second)) {sw.Restart();for(int i=0;i<60;i++){capture.Grab(area);g1.DrawImage(capture.Bitmap,new Rectangle(0,0,150,50),new Rectangle(0,0,150,50),GraphicsUnit.Pixel);g2.DrawImage(capture.Bitmap,new Rectangle(0,0,150,50),new Rectangle(150,0,150,50),GraphicsUnit.Pixel);ocr.Read(first,0,0);ocr.Read(second,0,0);}lines.Add("Live shared capture + OCR (two 150x50 areas), unpaced iterations/s: "+(60/sw.Elapsed.TotalSeconds).ToString("F1",CultureInfo.InvariantCulture));}}catch(Exception ex){lines.Add("Live capture unavailable: "+ex.Message);}
        failures+=SteeringTests.Run(lines);lines.Add("Failures: "+failures);File.WriteAllLines(output,lines.ToArray(),Encoding.UTF8);return failures==0?0:1;
    }
}




