
using UnityEngine;

namespace CustomRenderPipline
{
    [System.Serializable]
    public struct uint2
    {
        public uint x;
        public uint y;
        /// <summary>uint2 zero value.</summary>
        public static readonly uint2 zero;
        public static readonly uint2 one = new uint2(1,1);

        public static uint2 operator *(uint2 a, uint b)
        {
            return new uint2(a.x * b, a.y * b);
        }

        public static uint2 operator *(uint2 a, int b)
        {
            return new uint2(a.x * (uint)b, a.y * (uint)b);
        }

        public static uint2 operator +(uint2 a, int b)
        {
            return new uint2(a.x + (uint)b, a.y + (uint)b);
        }

        public static uint2 operator +(int b, uint2 a)
        {
            return new uint2(a.x + (uint)b, a.y + (uint)b);
        }

        public static uint2 operator /(uint2 a, int b)
        {
            return new uint2(a.x / (uint)b, a.y / (uint)b);
        }

        /// <summary>Constructs a uint2 vector from two uint values.</summary>
        /// <param name="x">The constructed vector's x component will be set to this value.</param>
        /// <param name="y">The constructed vector's y component will be set to this value.</param>
        public uint2(uint x, uint y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [System.Serializable]
    public struct uint3
    {
        public uint x;
        public uint y;
        public uint z;

        public static readonly uint3 zero;

        public uint3(uint x, uint y, uint z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    [System.Serializable]
    public struct uint4
    {
        public uint x;
        public uint y;
        public uint z;
        public uint w;

        public static readonly uint4 zero;

        public uint4(uint x, uint y, uint z, uint w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }
    }

    [System.Serializable]
    public struct int4
    {
        int x;
        int y;
        int z;
        int w;
    }

    [System.Serializable]
    public struct float4
    {
        public float x;
        public float y;
        public float z;
        public float w;
        public static readonly float4 zero;

        public float4(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public float4(Vector4 ve)
        {
            this.x = ve.x;
            this.y = ve.y;
            this.z = ve.z;
            this.w = ve.w;
        }
    }

    [System.Serializable]
    public struct float3
    {
        public float x;
        public float y;
        public float z;
        public static readonly float3 zero;

        public float3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }
}