#ifndef DEFFER_TERRAIN_COMMON_INPUT
#define DEFFER_TERRAIN_COMMON_INPUT

//一个Node拆成8x8个Patch
#define PATCH_COUNT_PER_NODE 8

struct NodeDescriptor{
    uint branch;
};


struct RenderPatch{
    float2 position;
    uint4 lod;      //xyz:nodeLod, w:patch offset
};

struct Bounds{
    float3 minPosition;
    float3 maxPosition;
};


static uint _LodMapLevelSize[] = {1, 2, 4, 8, 16, 32};

#endif
