using System;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public class SavedPosition {
    public double Height, Azimuth;
    public SavedPosition(){}
    public SavedPosition(double height,double azimuth){Height=height;Azimuth=azimuth;}
    public static bool TryInput(string height,string azimuth,out SavedPosition position){
        position=null;decimal h,a;
        if(!decimal.TryParse(height.Trim().Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out h)||!decimal.TryParse(azimuth.Trim().Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out a))return false;
        if(h<0||h>360||a<0||a>360||decimal.Round(h,1)!=h||decimal.Round(a,1)!=a)return false;
        position=new SavedPosition((double)h,(double)a);return true;
    }
    public bool Valid {get{return !double.IsNaN(Height)&&!double.IsInfinity(Height)&&Height>=0&&Height<=360&&!double.IsNaN(Azimuth)&&!double.IsInfinity(Azimuth)&&Azimuth>=0&&Azimuth<=360;}}
    public override string ToString(){return String.Format(CultureInfo.CurrentCulture,"Высота {0:F1}°  •  Азимут {1:F1}°",Height,Azimuth);}
}
public enum AimKey { W,A,S,D }
[Flags]public enum AimKeys { None=0,W=1,A=2,S=4,D=8 }
public class AngleSample {
    public double Height,Azimuth,TimeMs;public long Frame;public bool Valid;
    public static double Now {get{return Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency;}}
}
public static class AimMath {
    public static double AzimuthError(double target,double actual){return ((target-actual)%360+540)%360-180;}
    public static bool Exact(SavedPosition target,AngleSample current){return current!=null&&current.Valid&&(decimal)target.Height==(decimal)current.Height&&(decimal)target.Azimuth==(decimal)current.Azimuth;}
    public static bool Stable(AngleSample first,AngleSample second){return first!=null&&second!=null&&first.Valid&&second.Valid&&first.Frame!=second.Frame&&Math.Abs(first.Height-second.Height)<=0.11&&Math.Abs(AzimuthError(first.Azimuth,second.Azimuth))<=0.11;}
}
public interface IAimEnvironment {
    double Now {get;}
    AngleSample Sample();
    void Check();
    void Wait(int milliseconds,CancellationToken token);
    void SetKeys(AimKeys keys,CancellationToken token);
    void ReleaseKeys();
}
public static class AimRunner {
    static int Axis(double error,double velocity,double tolerance,double leadSeconds){
        if(Math.Abs(error)<=tolerance+0.0000001)return 0;
        if(Math.Sign(velocity)==Math.Sign(error)&&Math.Abs(velocity)*leadSeconds>=Math.Abs(error)-tolerance*0.5)return 0;
        return Math.Sign(error);
    }
    public static void Run(IAimEnvironment environment,SavedPosition target,int leadMs,CancellationToken token,Action<string> progress){
        if(target==null||!target.Valid)throw new InvalidOperationException("Некорректное сохранённое положение.");
        const double tolerance=0;
        double start=environment.Now,missing=-1,settled=-1,lastProgress=start,best=double.PositiveInfinity,lastReport=-1000;
        double velocityH=0,velocityA=0;AngleSample previous=null;int validFrames=0;AimKeys held=AimKeys.None;
        try{
            while(environment.Now-start<60000){
                token.ThrowIfCancellationRequested();environment.Check();AngleSample current=environment.Sample();
                if(current==null||!current.Valid||environment.Now-current.TimeMs>200||environment.Now-current.TimeMs<0){
                    environment.ReleaseKeys();held=AimKeys.None;previous=null;validFrames=0;settled=-1;velocityH=velocityA=0;
                    if(missing<0)missing=environment.Now;
                    if(environment.Now-missing>=900)throw new InvalidOperationException("Нет достоверных показаний обоих углов. Клавиши отпущены.");
                    environment.Wait(10,token);continue;
                }
                missing=-1;
                if(previous!=null&&current.Frame==previous.Frame){environment.Wait(5,token);continue;}
                if(previous!=null){double seconds=(current.TimeMs-previous.TimeMs)/1000;
                    if(seconds>0&&seconds<0.25){velocityH=0.45*((current.Height-previous.Height)/seconds)+0.55*velocityH;velocityA=0.45*(AimMath.AzimuthError(current.Azimuth,previous.Azimuth)/seconds)+0.55*velocityA;}
                }
                previous=current;validFrames++;
                if(validFrames<2){environment.ReleaseKeys();environment.Wait(10,token);continue;}
                double eh=target.Height-current.Height,ea=AimMath.AzimuthError(target.Azimuth,current.Azimuth);
                double error=Math.Sqrt(eh*eh+ea*ea);
                if(error<best-0.08){best=error;lastProgress=environment.Now;}
                bool within=AimMath.Exact(target,current);
                if(within){environment.ReleaseKeys();held=AimKeys.None;if(settled<0)settled=environment.Now;
                    if(environment.Now-settled>=400){progress("Точное совпадение подтверждено: "+target);return;}
                }else {
                    settled=-1;
                    int h=Axis(eh,velocityH,tolerance,leadMs/1000.0);
                    int a=Axis(ea,velocityA,tolerance,leadMs/1000.0);
                    held=(h>0?AimKeys.W:h<0?AimKeys.S:AimKeys.None)|(a>0?AimKeys.D:a<0?AimKeys.A:AimKeys.None);
                    environment.SetKeys(held,token);
                }
                if(environment.Now-lastProgress>6000)throw new InvalidOperationException("Точного совпадения добиться не удалось. Клавиши отпущены.");
                if(environment.Now-lastReport>=100){progress(String.Format(CultureInfo.CurrentCulture,"Высота {0:F1}°, азимут {1:F1}°\nУдержание: {2}. F8 — остановка.",current.Height,current.Azimuth,held==AimKeys.None?"нет":held.ToString()));lastReport=environment.Now;}
                environment.Wait(10,token);
            }
            throw new InvalidOperationException("Время возврата истекло (60 секунд). Клавиши отпущены.");
        }finally{environment.ReleaseKeys();}
    }
}
public interface IKeyOutput {void Send(AimKey key,bool up);}
public class NativeKeyOutput : IKeyOutput {public void Send(AimKey key,bool up){GameInput.Send(key,up);}}
public class HeldKeyboard {
    readonly IKeyOutput output;public AimKeys Held {get;private set;}
    public HeldKeyboard(IKeyOutput output){this.output=output;}
    public static AimKeys Flag(AimKey key){return key==AimKey.W?AimKeys.W:key==AimKey.S?AimKeys.S:key==AimKey.A?AimKeys.A:AimKeys.D;}
    public void Set(AimKeys wanted){
        if((wanted&(AimKeys.W|AimKeys.S))==(AimKeys.W|AimKeys.S)||(wanted&(AimKeys.A|AimKeys.D))==(AimKeys.A|AimKeys.D))throw new ArgumentException("Противоположные клавиши одновременно недопустимы.");
        Exception error=null;
        foreach(AimKey key in new[]{AimKey.W,AimKey.A,AimKey.S,AimKey.D})if((Held&Flag(key))!=0&&(wanted&Flag(key))==0){try{output.Send(key,true);Held&=~Flag(key);}catch(Exception ex){error=ex;}}
        if(error!=null)throw error;
        foreach(AimKey key in new[]{AimKey.W,AimKey.A,AimKey.S,AimKey.D})if((Held&Flag(key))==0&&(wanted&Flag(key))!=0){output.Send(key,false);Held|=Flag(key);}
    }
}
public static class GameInput {
    [StructLayout(LayoutKind.Sequential)]public struct Keyboard {public ushort Vk,Scan;public uint Flags,Time;public IntPtr Extra;}
    [StructLayout(LayoutKind.Explicit,Size=40)]public struct Input {[FieldOffset(0)]public uint Type;[FieldOffset(8)]public Keyboard Key;}
    [StructLayout(LayoutKind.Sequential)]public struct Rect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",SetLastError=true)]static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetClassName(IntPtr window,StringBuilder value,int count);
    [DllImport("user32.dll")]public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)]public static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]public static extern bool UnregisterHotKey(IntPtr window,int id);
    public static ushort Scan(AimKey key){return key==AimKey.W?(ushort)0x11:key==AimKey.S?(ushort)0x1F:key==AimKey.A?(ushort)0x1E:(ushort)0x20;}
    public static Input Event(AimKey key,bool up){return new Input{Type=1,Key=new Keyboard{Scan=Scan(key),Flags=(uint)(8|(up?2:0))}};}
    public static void Send(AimKey key,bool up){if(SendInput(1,new[]{Event(key,up)},Marshal.SizeOf(typeof(Input)))!=1)throw new InvalidOperationException("Windows не приняла нажатие клавиши. Проверьте права запуска игры и программы.");}
    public static bool Down(int vk){return (GetAsyncKeyState(vk)&0x8000)!=0;}
    public static Rectangle Bounds(IntPtr window){Rect r;if(!GetWindowRect(window,out r))throw new InvalidOperationException("Окно игры недоступно.");return Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);}
}
public class DesktopAimEnvironment : IAimEnvironment,IDisposable {
    readonly Func<AngleSample> read;readonly Func<bool> captureActive;readonly IntPtr target;readonly uint process;readonly Rectangle bounds;
    readonly object holdLock=new object();readonly HeldKeyboard keyboard=new HeldKeyboard(new NativeKeyOutput());readonly System.Threading.Timer watchdog;
    double heartbeat;volatile string fault;bool disposed;
    public DesktopAimEnvironment(Func<AngleSample> read,Func<bool> captureActive,IntPtr target){this.read=read;this.captureActive=captureActive;this.target=target;GameInput.GetWindowThreadProcessId(target,out process);bounds=GameInput.Bounds(target);heartbeat=Now;watchdog=new System.Threading.Timer(_=>Watchdog(),null,20,20);}
    public double Now {get{return AngleSample.Now;}}
    public AngleSample Sample(){return read();}
    public void Check(){uint currentProcess;GameInput.GetWindowThreadProcessId(target,out currentProcess);
        if(fault!=null)throw new InvalidOperationException(fault);
        if(!captureActive())throw new InvalidOperationException("Захват остановлен.");
        if(!GameInput.IsWindow(target)||currentProcess!=process||GameInput.GetForegroundWindow()!=target)throw new InvalidOperationException("Игра потеряла фокус. Возврат остановлен.");
        if(GameInput.Bounds(target)!=bounds)throw new InvalidOperationException("Окно переместилось. Проверьте выбранные области.");
        if(GameInput.Down(0x77)||GameInput.Down(0x1B))throw new OperationCanceledException();
        if(GameInput.Down(0x10)||GameInput.Down(0x11)||GameInput.Down(0x12)||GameInput.Down(0x5B)||GameInput.Down(0x5C))throw new InvalidOperationException("Нажата служебная клавиша. Возврат остановлен.");
    }
    public void Wait(int milliseconds,CancellationToken token){Stopwatch watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){token.ThrowIfCancellationRequested();Check();token.WaitHandle.WaitOne(Math.Min(10,Math.Max(1,milliseconds-(int)watch.ElapsedMilliseconds)));}}
    public void SetKeys(AimKeys keys,CancellationToken token){
        token.ThrowIfCancellationRequested();Check();lock(holdLock){
            if(disposed)throw new ObjectDisposedException("DesktopAimEnvironment");
            AngleSample sample=read();
            if(keys!=AimKeys.None&&(sample==null||!sample.Valid||Now-sample.TimeMs>200)){keyboard.Set(AimKeys.None);return;}
            foreach(AimKey key in new[]{AimKey.W,AimKey.A,AimKey.S,AimKey.D}){int vk=key==AimKey.W?0x57:key==AimKey.S?0x53:key==AimKey.A?0x41:0x44;if((keyboard.Held&HeldKeyboard.Flag(key))==0&&GameInput.Down(vk))throw new InvalidOperationException("Нажата WASD вручную. Возврат остановлен.");}
            Check();keyboard.Set(keys);heartbeat=Now;
        }
    }
    public void ReleaseKeys(){lock(holdLock){keyboard.Set(AimKeys.None);heartbeat=Now;}}
    void Watchdog(){lock(holdLock){
        if(disposed||keyboard.Held==AimKeys.None)return;
        try{Check();AngleSample sample=read();
            if(Now-heartbeat>300){keyboard.Set(AimKeys.None);fault="Управление не обновляется. Клавиши отпущены.";}
            else if(sample==null||!sample.Valid||Now-sample.TimeMs>200)keyboard.Set(AimKeys.None);
        }catch(Exception ex){fault=ex.Message;try{keyboard.Set(AimKeys.None);}catch{}}
    }}
    public void Dispose(){watchdog.Dispose();lock(holdLock){disposed=true;keyboard.Set(AimKeys.None);}}
}
public partial class MainForm {
    volatile AngleSample liveAngle,previousAngle;Thread navigationThread;CancellationTokenSource navigationCancel;
    volatile string navigationMessage="F6 — положение 1, F7 — положение 2. F8 — остановка.";bool hotkeyReady;bool[] positionHotkeyReady=new bool[2];volatile SavedPosition confirmedPosition;
    Label navigationStatus;Label[] positionLabels=new Label[2];Button[] positionSave=new Button[2],positionReturn=new Button[2],positionManual=new Button[2];NumericUpDown aimPulse;
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);hotkeyReady=GameInput.RegisterHotKey(Handle,803,0x4000,0x77);positionHotkeyReady[0]=GameInput.RegisterHotKey(Handle,801,0x4000,0x75);positionHotkeyReady[1]=GameInput.RegisterHotKey(Handle,802,0x4000,0x76);}
    protected override void OnShown(EventArgs e){base.OnShown(e);UpdateSteeringUi();}
    protected override void OnHandleDestroyed(EventArgs e){CancelNavigation();GameInput.UnregisterHotKey(Handle,803);GameInput.UnregisterHotKey(Handle,801);GameInput.UnregisterHotKey(Handle,802);base.OnHandleDestroyed(e);}
    protected override void WndProc(ref Message message){if(message.Msg==0x0312){int id=message.WParam.ToInt32();if(id==803){CancelNavigation();navigationMessage="Остановлено по F8.";}else if(id==801||id==802)StartNavigation(id-801,true);}base.WndProc(ref message);}
    void BuildSteering(){
        GroupBox group=new GroupBox{Text="Положения и возврат WASD",Left=730,Top=15,Width=350,Height=595};Controls.Add(group);
        for(int i=0;i<2;i++){int index=i,top=25+i*130;
            group.Controls.Add(new Label{Text="Положение "+(i+1),Left=12,Top=top+5,Width=155,Height=23});
            positionManual[i]=new Button{Text="Ввести вручную…",Left=178,Top=top,Width=155,Height=28};positionManual[i].Click+=(s,e)=>EditPosition(index);group.Controls.Add(positionManual[i]);
            positionLabels[i]=new Label{Left=12,Top=top+35,Width=325,Height=25};group.Controls.Add(positionLabels[i]);
            positionSave[i]=new Button{Text="Запомнить "+(i+1),Left=12,Top=top+65,Width=153,Height=40};positionSave[i].Click+=(s,e)=>Remember(index);group.Controls.Add(positionSave[i]);
            positionReturn[i]=new Button{Text="Вернуться "+(i+1)+" (F"+(i+6)+")",Left=178,Top=top+65,Width=155,Height=40};positionReturn[i].Click+=(s,e)=>StartNavigation(index);group.Controls.Add(positionReturn[i]);
        }
        group.Controls.Add(new Label{Text="Цель: точное совпадение обоих чисел",Left=12,Top=286,Width=325,Height=30});
        group.Controls.Add(new Label{Text="Опережение, мс:",Left=12,Top=326,Width=200});aimPulse=new NumericUpDown{Left=225,Top=322,Width=108,Minimum=0,Maximum=250,Value=Math.Max(0,Math.Min(250,settings.LeadMs))};group.Controls.Add(aimPulse);
        group.Controls.Add(new Label{Text="F6 / F7 — возврат сразу из игры.\nКнопкой — переключитесь в игру за 3 с.",Left=12,Top=368,Width=325,Height=52});
        Button cancel=new Button{Text="Остановить возврат — F8",Left=12,Top=427,Width=321,Height=40};cancel.Click+=(s,e)=>{CancelNavigation();navigationMessage="Возврат остановлен.";};group.Controls.Add(cancel);
        navigationStatus=new Label{Left=12,Top=485,Width=325,Height=100};group.Controls.Add(navigationStatus);UpdateSteeringUi();
    }
    void SetLiveAngle(Reading height,Reading azimuth,long frame){double h=0,a=0;bool valid=height.Text!=null&&azimuth.Text!=null&&height.Confidence>=0.78&&azimuth.Confidence>=0.78&&double.TryParse(height.Text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out h)&&double.TryParse(azimuth.Text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out a);
        previousAngle=liveAngle;liveAngle=new AngleSample{Height=h,Azimuth=a,Frame=frame,TimeMs=AngleSample.Now,Valid=valid};
    }
    SavedPosition Position(int index){return index==0?settings.Position1:settings.Position2;}
    void EditPosition(int index){CancelNavigation();SavedPosition existing=Position(index);
        using(Form dialog=new Form{Text="Положение "+(index+1)+" — ручной ввод",ClientSize=new Size(370,190),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
            dialog.Controls.Add(new Label{Text="Высота, градусы",Left=18,Top=22,Width=175});dialog.Controls.Add(new Label{Text="Азимут, градусы",Left=18,Top=67,Width=175});
            TextBox height=new TextBox{Left=205,Top=18,Width=140,Text=(existing==null?0:existing.Height).ToString("F1",CultureInfo.CurrentCulture)};
            TextBox azimuth=new TextBox{Left=205,Top=63,Width=140,Text=(existing==null?0:existing.Azimuth).ToString("F1",CultureInfo.CurrentCulture)};dialog.Controls.Add(height);dialog.Controls.Add(azimuth);
            dialog.Controls.Add(new Label{Text="Шаг ввода 0,1° — как на показаниях игры.",Left=18,Top=107,Width=330});
            SavedPosition saved=null;Button apply=new Button{Text="Сохранить",Left=205,Top=143,Width=140};dialog.Controls.Add(apply);dialog.AcceptButton=apply;
            apply.Click+=(s,e)=>{if(!SavedPosition.TryInput(height.Text,azimuth.Text,out saved)){MessageBox.Show(dialog,"Введите числа от 0 до 360, максимум один знак после точки или запятой.");return;}dialog.DialogResult=DialogResult.OK;dialog.Close();};
            if(dialog.ShowDialog(this)==DialogResult.OK){if(index==0)settings.Position1=saved;else settings.Position2=saved;
                try{Save();navigationMessage="Положение "+(index+1)+" задано вручную. "+saved;}catch(Exception ex){navigationMessage=ex.Message;}
            }
        }
    }
    void Remember(int index){confirmedPosition=null;AngleSample current=liveAngle,previous=previousAngle;
        if(worker==null||stopping||current==null||AngleSample.Now-current.TimeMs>250||!AimMath.Stable(previous,current)){navigationMessage="Остановите наведение и дождитесь устойчивого распознавания обоих чисел.";return;}
        SavedPosition saved=new SavedPosition(current.Height,current.Azimuth);if(index==0)settings.Position1=saved;else settings.Position2=saved;
        try{Save();navigationMessage="Положение "+(index+1)+" сохранено. "+saved;}catch(Exception ex){navigationMessage="Не удалось сохранить: "+ex.Message;}
    }
    void UpdateSteeringUi(){if(navigationThread!=null&&!navigationThread.IsAlive){navigationThread=null;if(navigationCancel!=null){navigationCancel.Dispose();navigationCancel=null;}}
        bool active=navigationThread!=null;aimPulse.Enabled=!active;
        if(!active&&confirmedPosition!=null){AngleSample sample=liveAngle;if(sample!=null&&sample.Valid&&AngleSample.Now-sample.TimeMs<200&&!AimMath.Exact(confirmedPosition,sample)){confirmedPosition=null;navigationMessage="После подтверждения углы изменились.\nСейчас: "+new SavedPosition(sample.Height,sample.Azimuth);}}
        for(int i=0;i<2;i++){SavedPosition position=Position(i);positionLabels[i].Text=position==null?"Не запомнено":position.ToString();positionManual[i].Enabled=!active;positionSave[i].Enabled=!active&&worker!=null&&!stopping;positionReturn[i].Enabled=!active&&hotkeyReady&&position!=null&&position.Valid&&ocr!=null;}
        string conflicts=(!positionHotkeyReady[0]?"F6 занята. ":"")+(!positionHotkeyReady[1]?"F7 занята. ":"");
        navigationStatus.Text=hotkeyReady?navigationMessage+(conflicts.Length>0?"\n"+conflicts:""):"F8 занята другой программой. Освободите F8 и перезапустите приложение для возврата.";
    }
    void CancelNavigation(){confirmedPosition=null;if(navigationCancel!=null)navigationCancel.Cancel();if(navigationThread!=null&&navigationThread!=Thread.CurrentThread){navigationThread.Join(1000);if(!navigationThread.IsAlive){navigationThread=null;navigationCancel.Dispose();navigationCancel=null;}}}
    void StartNavigation(int index,bool fromHotkey=false){
        CancelNavigation();SavedPosition position=Position(index);if(position==null||!position.Valid||!hotkeyReady)return;
        if(ocr==null){navigationMessage="Дождитесь подготовки распознавания.";return;}
        IntPtr hotkeyTarget=fromHotkey?GameInput.GetForegroundWindow():IntPtr.Zero;
        if(fromHotkey){uint ownPid;GameInput.GetWindowThreadProcessId(hotkeyTarget,out ownPid);if(ownPid==(uint)Process.GetCurrentProcess().Id){navigationMessage="F6/F7 нажимайте из окна игры. Здесь можно использовать кнопки возврата.";return;}}
        if(worker==null)Start();if(worker==null||stopping)return;
        settings.Tolerance=0;settings.LeadMs=(int)aimPulse.Value;try{Save();}catch(Exception ex){navigationMessage=ex.Message;return;}
        int lead=settings.LeadMs;navigationCancel=new CancellationTokenSource();CancellationToken token=navigationCancel.Token;
        navigationThread=new Thread(()=>{
            try{if(!fromHotkey)for(int seconds=3;seconds>0;seconds--){navigationMessage="Возврат к положению "+(index+1)+" через "+seconds+" с.\nПереключитесь в игру. F8 — отмена.";if(token.WaitHandle.WaitOne(1000))token.ThrowIfCancellationRequested();}
                token.ThrowIfCancellationRequested();IntPtr target=fromHotkey?hotkeyTarget:GameInput.GetForegroundWindow();uint pid;GameInput.GetWindowThreadProcessId(target,out pid);StringBuilder name=new StringBuilder(256);GameInput.GetClassName(target,name,name.Capacity);
                if(target==IntPtr.Zero||pid==(uint)Process.GetCurrentProcess().Id||name.ToString()=="Progman"||name.ToString()=="WorkerW"||name.ToString()=="Shell_TrayWnd")throw new InvalidOperationException("Игра не выбрана. После нажатия переключитесь в её окно за 3 секунды.");
                using(var environment=new DesktopAimEnvironment(()=>liveAngle,()=>worker!=null&&!stopping,target))AimRunner.Run(environment,position,lead,token,message=>navigationMessage=message);
                confirmedPosition=position;
            }catch(OperationCanceledException){navigationMessage="Возврат остановлен.";}catch(Exception ex){navigationMessage=ex.Message;}
        }){IsBackground=true,Name="WASD return"};navigationThread.Start();
    }
}
