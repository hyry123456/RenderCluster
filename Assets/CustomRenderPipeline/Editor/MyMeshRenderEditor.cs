using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomRenderPipline.MyRenderPipeline.Editor
{
    [CustomEditor(typeof(MyMeshRender))]
    public class MyMeshRenderEditor : UnityEditor.Editor
    {

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            if (GUILayout.Button("Save Data"))
            {
                var example = target as MyMeshRender;
                example.SaveMeshData();
            }
            if (GUILayout.Button("Save Data Simple"))
            {
                var example = target as MyMeshRender;
                example.SaveMeshDataSimple();
            }


        }
    }
}