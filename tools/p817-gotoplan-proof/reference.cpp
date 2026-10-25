// P817 independent 1-D formula trace, not a native game binary capture.
// Extracted rules: Igc/common.cpp:1936-1983 (rest), :1343-1379 (Goto
// masks), :2022-2038 (finish/pivot/kill), :2231-2241 (throttle);
// Igc/shipIGC.cpp:2917-2934,2986-3029,3035 (velocity integration).
// Scripted fixture: mine approach, loaded warp trip, no-bay station entry.
// g++ -O0 -ffp-contract=off reference.cpp -o reference && ./reference > trace.csv
#include <cmath>
#include <cstdio>
int main() {
    const float dt=.05f, thrust=1000, mass=100, vmax=100, back=.5f;
    float z=1000, v=0; int phase=0;
    std::puts("tick,phase,mask,throttle,z,v");
    for (int tick=0; tick<5000; ++tick) {
        float goal=phase==1 ? -1000.f : 0.f;
        float speed=std::fabs(v), restDistance=0, rest=z;
        if (speed>.1f) {
            double vm=vmax*(double)back, k=thrust/(mass*(double)vmax);
            double mkt=std::log(vm/(speed+vm));
            restDistance=(float)((vm*mkt+speed)/k);
            rest += v*(restDistance/speed);
        }
        unsigned mask=0; float throttle=-1;
        if (phase==0) {
            float radius=15, centers=goal-z;
            if (centers*centers <= (radius+restDistance)*(radius+restDistance)) mask=4;
            else if ((goal-rest)*(goal-rest)<(radius+10)*(radius+10)) mask=restDistance<.5f ? 6 : 1;
        } else mask=64;
        if (!(mask & (1|2|4))) {
            float path=goal-z;
            if (path*path>=.1f && path<0) throttle=((mask&64)==0 && path*path<restDistance*restDistance) ? -1 : 1;
        }
        float tv=dt/mass;
        double f=std::exp(double(-thrust)*double(tv)/double(vmax));
        float drag=v*(float)((1-f)/double(tv));
        float desired=(-.5f*(1+throttle))*((speed>vmax)?speed:vmax);
        float engine=(desired-v)/tv+drag;
        float scaled=engine<=0 ? engine : engine/back;
        if (scaled*scaled>thrust*thrust) engine*=thrust/std::fabs(scaled);
        v+=tv*(engine-drag); z+=v*dt;
        std::printf("%d,%d,%u,%.9g,%.9g,%.9g\n",tick,phase,mask,throttle,z,v);
        if (phase==0 && z<45 && std::fabs(v)<1) phase=1; // mine, fill; depart for warp
        else if (phase==1 && z < -970) { phase=2; z=1000; v=-std::fabs(v)-15; } // native exit speed push, fsship.cpp:93,126
        else if (phase==2 && z<25) return 0; // unload at no-bay station
    }
    return 1;
}
