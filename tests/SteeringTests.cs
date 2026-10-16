using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

public static class SteeringTests {
    class RecordingOutput : IKeyOutput {
        public readonly List<string> Events=new List<string>();public AimKey? FailDown;
        public void Send(AimKey key,bool up){if(!up&&key==FailDown)throw new InvalidOperationException("injected failure");Events.Add(key+(up?" up":" down"));}
    }
    class Simulation : IAimEnvironment {
        public double Height=5,Azimuth=350,Clock,RateH=0.006,RateA=0.01,CouplingHA,CouplingAH,InertiaMs;
        public double InvalidFrom=double.MaxValue,InvalidUntil=double.MaxValue,StaleFrom=double.MaxValue,LoseFrom=double.MaxValue,CancelFrom=double.MaxValue;
        public int HoldThresholdMs;public bool NoMovement,Reverse,Simultaneous,MotionInInvalid;public int Downs,Updates,Releases;
        public double Quantum=0.1;
        public double LongestHold;public AimKeys Held;public long Frame=1;public CancellationTokenSource Cancel;
        double heldH,heldA,velocityH,velocityA;int signH,signA;
        public double Now{get{return Clock;}}
        public AngleSample Sample(){return new AngleSample{Height=Math.Round(Math.Round(Height/Quantum)*Quantum,1),Azimuth=(Math.Round(Math.Round(Azimuth/Quantum)*Quantum,1)%360+360)%360,TimeMs=Clock>=StaleFrom?StaleFrom:Clock,Frame=Frame,Valid=!(Clock>=InvalidFrom&&Clock<InvalidUntil)};}
        public void Check(){if(Clock>=LoseFrom)throw new InvalidOperationException("focus lost");}
        public void Wait(int milliseconds,CancellationToken token){
            for(int elapsed=0;elapsed<milliseconds;){token.ThrowIfCancellationRequested();Check();int step=Math.Min(5,milliseconds-elapsed);elapsed+=step;
                if(Held!=AimKeys.None&&Clock>=InvalidFrom&&Clock<InvalidUntil)MotionInInvalid=true;
                heldH=signH==0?0:heldH+step;heldA=signA==0?0:heldA+step;LongestHold=Math.Max(LongestHold,Math.Max(heldH,heldA));
                double h=heldH>=HoldThresholdMs?signH*RateH:0,a=heldA>=HoldThresholdMs?signA*RateA:0;
                if(NoMovement)h=a=0;if(Reverse){h=-h;a=-a;}
                double vh=h+a*CouplingAH,va=a+h*CouplingHA;
                double alpha=InertiaMs<=0?1:Math.Min(1,step/InertiaMs);velocityH+=(vh-velocityH)*alpha;velocityA+=(va-velocityA)*alpha;
                Height+=velocityH*step;Azimuth=(Azimuth+velocityA*step+360)%360;Clock+=step;Frame++;
                if(Clock>=CancelFrom&&Cancel!=null)Cancel.Cancel();
            }
        }
        public void SetKeys(AimKeys wanted,CancellationToken token){token.ThrowIfCancellationRequested();Check();Updates++;
            if((wanted&(AimKeys.W|AimKeys.S))!=0&&(wanted&(AimKeys.A|AimKeys.D))!=0)Simultaneous=true;
            int nextH=(wanted&AimKeys.W)!=0?1:(wanted&AimKeys.S)!=0?-1:0,nextA=(wanted&AimKeys.D)!=0?1:(wanted&AimKeys.A)!=0?-1:0;
            if(nextH!=signH){heldH=0;if(nextH!=0)Downs++;}if(nextA!=signA){heldA=0;if(nextA!=0)Downs++;}
            signH=nextH;signA=nextA;Held=wanted;
        }
        public void ReleaseKeys(){if(Held!=AimKeys.None)Releases++;Held=AimKeys.None;signH=signA=0;heldH=heldA=0;}
    }
    static bool Converges(Simulation e,SavedPosition target){AimRunner.Run(e,target,60,CancellationToken.None,message=>{});return AimMath.Exact(target,e.Sample())&&e.Held==AimKeys.None;}
    static bool Refuses(Simulation e,double maximumMs){try{AimRunner.Run(e,new SavedPosition(7,10),60,CancellationToken.None,message=>{});return false;}catch(InvalidOperationException){return e.Clock<=maximumMs&&e.Held==AimKeys.None;}}
    public static int Run(List<string> lines){int failures=0;
        Action<string,Func<bool>> check=(name,test)=>{bool ok=false;try{ok=test();}catch(Exception ex){lines.Add("Steering exception: "+ex.Message);}if(!ok)failures++;lines.Add("Steering "+name+": "+ok);};
        check("Simultaneous W and D across north",()=>{var e=new Simulation();return Converges(e,new SavedPosition(7,10))&&e.Simultaneous;});
        check("Simultaneous S and A",()=>{var e=new Simulation{Height=8,Azimuth=25};return Converges(e,new SavedPosition(3,15))&&e.Simultaneous;});
        check("Elevation changes azimuth; compensates concurrently",()=>{var e=new Simulation{CouplingHA=0.8};return Converges(e,new SavedPosition(12,350))&&e.Simultaneous;});
        check("Both axes mutually coupled with inertia",()=>{var e=new Simulation{CouplingHA=0.8,CouplingAH=0.15,InertiaMs=35};return Converges(e,new SavedPosition(12,10))&&e.Simultaneous;});
        check("A across north",()=>Converges(new Simulation{Height=8,Azimuth=2},new SavedPosition(8,358)));
        check("Keys requiring continuous hold over 120 ms",()=>{var e=new Simulation{HoldThresholdMs=120};return Converges(e,new SavedPosition(7,350))&&e.LongestHold>=120;});
        check("Already at target sends no downs",()=>{var e=new Simulation();return Converges(e,new SavedPosition(e.Height,e.Azimuth))&&e.Downs==0;});
        check("Screenshot near miss is not exact",()=>!AimMath.Exact(new SavedPosition(28.2,255.4),new AngleSample{Height=28,Azimuth=255.2,Valid=true}));
        check("Manual angles accept dot and comma",()=>{SavedPosition p;return SavedPosition.TryInput("28.2","255,4",out p)&&p.Height==28.2&&p.Azimuth==255.4;});
        check("Manual input rejects hidden decimals and invalid ranges",()=>{SavedPosition p;return !SavedPosition.TryInput("28.25","255.4",out p)&&!SavedPosition.TryInput("-1","255.4",out p)&&!SavedPosition.TryInput("28.2","361",out p);});
        check("One tenth error on either axis is not exact",()=>!AimMath.Exact(new SavedPosition(28.2,255.4),new AngleSample{Height=28.1,Azimuth=255.4,Valid=true})&&!AimMath.Exact(new SavedPosition(28.2,255.4),new AngleSample{Height=28.2,Azimuth=255.3,Valid=true}));
        check("Exact displayed target converges and confirms",()=>{var e=new Simulation{Height=28,Azimuth=255.2};return Converges(e,new SavedPosition(28.2,255.4));});
        check("Unreachable displayed value never reports success",()=>{var e=new Simulation{Quantum=0.2};bool success=false;try{AimRunner.Run(e,new SavedPosition(7.1,10),60,CancellationToken.None,message=>{if(message.StartsWith("Точное совпадение"))success=true;});return false;}catch(InvalidOperationException){return !success&&e.Held==AimKeys.None;}});
        check("Unknown OCR sends no keys",()=>{var e=new Simulation{InvalidFrom=0};return Refuses(e,1000)&&e.Downs==0;});
        check("Stale OCR releases held keys",()=>Refuses(new Simulation{StaleFrom=150},1500));
        check("Transient OCR loss releases then resumes",()=>{var e=new Simulation{InvalidFrom=200,InvalidUntil=400};return Converges(e,new SavedPosition(7,10))&&e.Releases>0&&!e.MotionInInvalid;});
        check("Prolonged OCR loss releases and stops",()=>Refuses(new Simulation{InvalidFrom=200},1200));
        check("Focus loss releases both axes",()=>Refuses(new Simulation{LoseFrom=100},110));
        check("No response stops without holding forever",()=>Refuses(new Simulation{NoMovement=true},6200));
        check("Wrong direction eventually stops",()=>Refuses(new Simulation{Reverse=true},6200));
        check("Cancellation releases both axes",()=>{using(var cancellation=new CancellationTokenSource()){var e=new Simulation{Cancel=cancellation,CancelFrom=120};try{AimRunner.Run(e,new SavedPosition(7,10),60,cancellation.Token,message=>{});return false;}catch(OperationCanceledException){return e.Clock<=125&&e.Held==AimKeys.None;}}});
        check("Continuous key states do not repeat key-down",()=>{var sink=new RecordingOutput();var k=new HeldKeyboard(sink);for(int i=0;i<100;i++)k.Set(AimKeys.W|AimKeys.D);bool ok=sink.Events.Count==2&&k.Held==(AimKeys.W|AimKeys.D);k.Set(AimKeys.None);return ok&&sink.Events.Count==4&&k.Held==AimKeys.None;});
        check("Reversal releases old pair before pressing new pair",()=>{var sink=new RecordingOutput();var k=new HeldKeyboard(sink);k.Set(AimKeys.W|AimKeys.D);k.Set(AimKeys.S|AimKeys.A);return sink.Events[2]=="W up"&&sink.Events[3]=="D up"&&sink.Events[4]=="A down"&&sink.Events[5]=="S down";});
        check("Partial input failure releases successfully held key",()=>{var sink=new RecordingOutput{FailDown=AimKey.D};var k=new HeldKeyboard(sink);try{k.Set(AimKeys.W|AimKeys.D);}catch(InvalidOperationException){}k.Set(AimKeys.None);return k.Held==AimKeys.None&&sink.Events.Count==2&&sink.Events[1]=="W up";});
        check("Opposite keys cannot be held together",()=>{var sink=new RecordingOutput();try{new HeldKeyboard(sink).Set(AimKeys.W|AimKeys.S);return false;}catch(ArgumentException){return sink.Events.Count==0;}});
        check("x64 keyboard event size and scan codes",()=>Marshal.SizeOf(typeof(GameInput.Input))==40&&GameInput.Event(AimKey.W,false).Key.Scan==0x11&&GameInput.Event(AimKey.D,true).Key.Flags==10);
        return failures;
    }
}
