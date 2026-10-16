#include <cstdio>
#include <cstdint>
struct FEDMESSAGE { uint16_t cbmsg; uint16_t fmid; };
struct FMD_CS_SWAP_PART : FEDMESSAGE { int16_t shipID; int16_t etOld; signed char mountOld; signed char mountNew; };
int main() { FMD_CS_SWAP_PART m{}; auto b=(char*)&m; printf("size=%zu align=%zu offsets=%td,%td,%td,%td\n",sizeof(m),alignof(FMD_CS_SWAP_PART),(char*)&m.shipID-b,(char*)&m.etOld-b,(char*)&m.mountOld-b,(char*)&m.mountNew-b); }
