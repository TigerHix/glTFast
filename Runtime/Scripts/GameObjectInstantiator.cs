﻿// Copyright 2020-2022 Andreas Atteneder
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

using System;
using System.Collections.Generic;
using GLTFast.Schema;
using Unity.Collections;
using UnityEngine;
using Camera = UnityEngine.Camera;
using Material = UnityEngine.Material;
using Mesh = UnityEngine.Mesh;

// #if UNITY_EDITOR && UNITY_ANIMATION
// using UnityEditor.Animations;
// #endif

namespace GLTFast {
    public class GameObjectInstantiator : IInstantiator {

        public class Settings {
            public bool skinUpdateWhenOffscreen = true;
            public int layer;
            public ComponentType mask = ComponentType.Mesh | ComponentType.Camera;
        }
        
        public class SceneInstance {
            public List<Camera> cameras { get; private set; }
            public List<Light> lights { get; private set; }

            public void AddCamera(Camera camera) {
                if (cameras == null) {
                    cameras = new List<Camera>();
                }
                cameras.Add(camera);
            }
            
            public void AddLight(Light light) {
                if (lights == null) {
                    lights = new List<Light>();
                }
                lights.Add(light);
            }
        }
        
        protected Settings settings;
        
        protected ICodeLogger logger;
        
        protected IGltfReadable gltf;
        
        protected Transform parent;

        protected Dictionary<uint,GameObject> nodes;

        /// <summary>
        /// Contains information about the latest instance of a glTF scene
        /// </summary>
        public SceneInstance sceneInstance { get; protected set; }
        
        public GameObjectInstantiator(
            IGltfReadable gltf,
            Transform parent,
            ICodeLogger logger = null,
            Settings settings = null
            )
        {
            this.gltf = gltf;
            this.parent = parent;
            this.logger = logger;
            this.settings = settings ?? new Settings();
        }

        public virtual void Init() {
            nodes = new Dictionary<uint, GameObject>();
            sceneInstance = new SceneInstance();
        }

        public void CreateNode(
            uint nodeIndex,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale
        ) {
            var go = new GameObject();
            go.transform.localScale = scale;
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.layer = settings.layer;
            nodes[nodeIndex] = go;
        }

        public void SetParent(uint nodeIndex, uint parentIndex) {
            if(nodes[nodeIndex]==null || nodes[parentIndex]==null ) {
                logger?.Error(LogCode.HierarchyInvalid);
                return;
            }
            nodes[nodeIndex].transform.SetParent(nodes[parentIndex].transform,false);
        }

        public virtual void SetNodeName(uint nodeIndex, string name) {
            nodes[nodeIndex].name = name ?? $"Node-{nodeIndex}";
        }

        public virtual void AddPrimitive(
            uint nodeIndex,
            string meshName,
            Mesh mesh,
            int[] materialIndices,
            uint[] joints = null,
            uint? rootJoint = null,
            float[] morphTargetWeights = null,
            int primitiveNumeration = 0
        ) {
            if ((settings.mask & ComponentType.Mesh) == 0) {
                return;
            }

            GameObject meshGo;
            if(primitiveNumeration==0) {
                // Use Node GameObject for first Primitive
                meshGo = nodes[nodeIndex];
            } else {
                meshGo = new GameObject(meshName);
                meshGo.transform.SetParent(nodes[nodeIndex].transform,false);
                meshGo.layer = settings.layer;
            }

            Renderer renderer;

            var hasMorphTargets = mesh.blendShapeCount > 0;
            if(joints==null && !hasMorphTargets) {
                var mf = meshGo.AddComponent<MeshFilter>();
                mf.mesh = mesh;
                var mr = meshGo.AddComponent<MeshRenderer>();
                renderer = mr;
            } else {
                var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
                smr.updateWhenOffscreen = settings.skinUpdateWhenOffscreen;
                if (joints != null) {
                    var bones = new Transform[joints.Length];
                    for (var j = 0; j < bones.Length; j++)
                    {
                        var jointIndex = joints[j];
                        bones[j] = nodes[jointIndex].transform;
                    }
                    smr.bones = bones;
                    if (rootJoint.HasValue) {
                        smr.rootBone = nodes[rootJoint.Value].transform;
                    }
                }
                smr.sharedMesh = mesh;
                if (morphTargetWeights!=null) {
                    for (var i = 0; i < morphTargetWeights.Length; i++) {
                        var weight = morphTargetWeights[i];
                        smr.SetBlendShapeWeight(i, weight);
                    }
                }
                renderer = smr;
            }

            var materials = new Material[materialIndices.Length];
            for (var index = 0; index < materials.Length; index++) {
                 var material = gltf.GetMaterial(materialIndices[index]) ?? gltf.GetDefaultMaterial();
                 materials[index] = material;
            }

            renderer.sharedMaterials = materials;
        }

        public void AddPrimitiveInstanced(
            uint nodeIndex,
            string meshName,
            Mesh mesh,
            int[] materialIndices,
            uint instanceCount,
            NativeArray<Vector3>? positions,
            NativeArray<Quaternion>? rotations,
            NativeArray<Vector3>? scales,
            int primitiveNumeration = 0
        ) {
            if ((settings.mask & ComponentType.Mesh) == 0) {
                return;
            }
            
            var materials = new Material[materialIndices.Length];
            for (var index = 0; index < materials.Length; index++) {
                var material = gltf.GetMaterial(materialIndices[index]) ?? gltf.GetDefaultMaterial();
                material.enableInstancing = true;
                materials[index] = material;
            }

            for (var i = 0; i < instanceCount; i++) {
                var meshGo = new GameObject( $"{meshName}_i{i}" );
                meshGo.layer = settings.layer;
                var t = meshGo.transform;
                t.SetParent(nodes[nodeIndex].transform,false);
                t.localPosition = positions?[i] ?? Vector3.zero;
                t.localRotation = rotations?[i] ?? Quaternion.identity;
                t.localScale = scales?[i] ?? Vector3.one;
                
                var mf = meshGo.AddComponent<MeshFilter>();
                mf.mesh = mesh;
                Renderer renderer = meshGo.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = materials;
            }
        }

        public void AddCamera(uint nodeIndex, uint cameraIndex) {
            if ((settings.mask & ComponentType.Camera) == 0) {
                return;
            }
            var camera = gltf.GetSourceCamera(cameraIndex);
            switch (camera.typeEnum) {
            case Schema.Camera.Type.Orthographic:
                var o = camera.orthographic;
                AddCameraOrthographic(
                    nodeIndex,
                    o.znear,
                    o.zfar >=0 ? o.zfar : (float?) null,
                    o.xmag,
                    o.ymag,
                    camera.name
                );
                break;
            case Schema.Camera.Type.Perspective:
                var p = camera.perspective;
                AddCameraPerspective(
                    nodeIndex,
                    p.yfov,
                    p.znear,
                    p.zfar,
                    p.aspectRatio>0 ? p.aspectRatio : (float?)null,
                    camera.name
                );
                break;
            }
        }

        void AddCameraPerspective(
            uint nodeIndex,
            float verticalFieldOfView,
            float nearClipPlane,
            float farClipPlane,
            float? aspectRatio,
            string cameraName
        ) {
            var cam = CreateCamera(nodeIndex,cameraName,out var localScale);

            cam.orthographic = false;

            cam.fieldOfView = verticalFieldOfView * Mathf.Rad2Deg;
            cam.nearClipPlane = nearClipPlane * localScale;
            cam.farClipPlane = farClipPlane * localScale;

            // // If the aspect ratio is given and does not match the
            // // screen's aspect ratio, the viewport rect is reduced
            // // to match the glTFs aspect ratio (box fit)
            // if (aspectRatio.HasValue) {
            //     cam.rect = GetLimitedViewPort(aspectRatio.Value);
            // }
        }

        void AddCameraOrthographic(
            uint nodeIndex,
            float nearClipPlane,
            float? farClipPlane,
            float horizontal,
            float vertical,
            string cameraName
        ) {
            var cam = CreateCamera(nodeIndex,cameraName,out var localScale);
            
            var farValue = farClipPlane ?? float.MaxValue;

            cam.orthographic = true;
            cam.nearClipPlane = nearClipPlane * localScale;
            cam.farClipPlane = farValue * localScale;
            cam.orthographicSize = vertical; // Note: Ignores `horizontal`

            // Custom projection matrix
            // Ignores screen's aspect ratio
            cam.projectionMatrix = Matrix4x4.Ortho(
                -horizontal,
                horizontal, 
                -vertical,
                vertical,
                nearClipPlane,
                farValue
            );

            // // If the aspect ratio does not match the
            // // screen's aspect ratio, the viewport rect is reduced
            // // to match the glTFs aspect ratio (box fit)
            // var aspectRatio = horizontal / vertical;
            // cam.rect = GetLimitedViewPort(aspectRatio);
        }

        /// <summary>
        /// Creates a camera component on the given node and returns an approximated
        /// local-to-world scale factor, required to counter-act that Unity scales
        /// near- and far-clipping-planes via Transform.
        /// </summary>
        /// <param name="nodeIndex">Node's index</param>
        /// <param name="cameraName">Camera's name</param>
        /// <param name="localScale">Approximated local-to-world scale factor</param>
        /// <returns>The newly created Camera component</returns>
        Camera CreateCamera(uint nodeIndex,string cameraName, out float localScale) {
            var cameraParent = nodes[nodeIndex];
            var camGo = new GameObject(cameraName ?? $"{cameraParent.name}-Camera" ?? $"Camera-{nodeIndex}");
            camGo.layer = settings.layer;
            var camTrans = camGo.transform;
            var parentTransform = cameraParent.transform;
            camTrans.SetParent(parentTransform,false);
            var tmp =Quaternion.Euler(0, 180, 0);
            camTrans.localRotation= tmp;
            var cam = camGo.AddComponent<Camera>();

            // By default, imported cameras are not enabled by default
            cam.enabled = false;

            sceneInstance.AddCamera(cam);

            var parentScale = parentTransform.localToWorldMatrix.lossyScale;
            localScale = (parentScale.x + parentScale.y + parentScale.y) / 3; 
            
            return cam;
        }

        // static Rect GetLimitedViewPort(float aspectRatio) {
        //     var screenAspect = Screen.width / (float)Screen.height;
        //     if (Mathf.Abs(1 - (screenAspect / aspectRatio)) <= math.EPSILON) {
        //         // Identical aspect ratios
        //         return new Rect(0,0,1,1);
        //     }
        //     if (aspectRatio < screenAspect) {
        //         var w = aspectRatio / screenAspect;
        //         return new Rect((1 - w) / 2, 0, w, 1f);
        //     } else {
        //         var h = screenAspect / aspectRatio;
        //         return new Rect(0, (1 - h) / 2, 1f, h);
        //     }
        // }

        public void AddLightPunctual(
            uint nodeIndex,
            uint lightIndex
        ) {
            if ((settings.mask & ComponentType.Light) == 0) {
                return;
            }
            var lightGameObject = nodes[nodeIndex];
            var lightSource = gltf.GetSourceLightPunctual(lightIndex);

            if (lightSource.typeEnum != LightPunctual.Type.Point) {
                // glTF lights' direction is flipped, compared with Unity's, so
                // we're adding a rotated child GameObject to counter act.
                var tmp = new GameObject($"{lightGameObject.name}_Orientation");
                tmp.transform.SetParent(lightGameObject.transform,false);
                tmp.transform.localEulerAngles = new Vector3(0, 180, 0);
                lightGameObject = tmp;
            }
            var light = lightGameObject.AddComponent<Light>();

            switch (lightSource.typeEnum) {
                case LightPunctual.Type.Unknown:
                    break;
                case LightPunctual.Type.Spot:
                    light.type = LightType.Spot;
                    break;
                case LightPunctual.Type.Directional:
                    light.type = LightType.Directional;
                    break;
                case LightPunctual.Type.Point:
                    light.type = LightType.Point;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
            light.color = lightSource.lightColor;
            light.intensity = lightSource.intensity;
            if (lightSource.range > 0) {
                light.range = lightSource.range;
            }

            if (lightSource.typeEnum == LightPunctual.Type.Spot) {
                light.spotAngle = lightSource.spot.outerConeAngle * Mathf.Rad2Deg * 2f;
                light.innerSpotAngle = lightSource.spot.innerConeAngle * Mathf.Rad2Deg * 2f;
            }
            
            sceneInstance.AddLight(light);
        }
        
        public virtual void AddScene(
            string name,
            uint[] nodeIndices
#if UNITY_ANIMATION
            ,AnimationClip[] animationClips
#endif // UNITY_ANIMATION
            )
        {
            var go = new GameObject(name ?? "Scene");
            go.transform.SetParent( parent, false);
            go.layer = settings.layer;

            if (nodeIndices != null) {
                foreach(var nodeIndex in nodeIndices) {
                    if (nodes[nodeIndex] != null) {
                        nodes[nodeIndex].transform.SetParent( go.transform, false );
                    }
                }
            }

#if UNITY_ANIMATION
            if (animationClips != null) {
                // we want to create an Animator for non-legacy clips, and an Animation component for legacy clips.
                var isLegacyAnimation = animationClips.Length > 0 && animationClips[0].legacy;
// #if UNITY_EDITOR
//                 // This variant creates a Mecanim Animator and AnimationController
//                 // which does not work at runtime. It's kept for potential Editor import usage
//                 if(!isLegacyAnimation) {
//                     var animator = go.AddComponent<Animator>();
//                     var controller = new UnityEditor.Animations.AnimatorController();
//                     controller.name = animator.name;
//                     controller.AddLayer("Default");
//                     controller.layers[0].defaultWeight = 1;
//                     for (var index = 0; index < animationClips.Length; index++) {
//                         var clip = animationClips[index];
//                         // controller.AddLayer(clip.name);
//                         // controller.layers[index].defaultWeight = 1;
//                         var state = controller.AddMotion(clip, 0);
//                         controller.AddParameter("Test", AnimatorControllerParameterType.Bool);
//                         // var stateMachine = controller.layers[0].stateMachine;
//                         // UnityEditor.Animations.AnimatorState entryState = null;
//                         // var state = stateMachine.AddState(clip.name);
//                         // state.motion = clip;
//                         // var loopTransition = state.AddTransition(state);
//                         // loopTransition.hasExitTime = true;
//                         // loopTransition.duration = 0;
//                         // loopTransition.exitTime = 0;
//                         // entryState = state;
//                         // stateMachine.AddEntryTransition(entryState);
//                         // UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath
//                     }
//                     
//                     animator.runtimeAnimatorController = controller;
//                     
//                     // for (var index = 0; index < animationClips.Length; index++) {
//                     //     controller.layers[index].blendingMode = UnityEditor.Animations.AnimatorLayerBlendingMode.Additive;
//                     //     animator.SetLayerWeight(index,1);
//                     // }
//                 }
// #endif // UNITY_EDITOR

                if(isLegacyAnimation) {
                    var animation = go.AddComponent<Animation>();
                    
                    for (var index = 0; index < animationClips.Length; index++) {
                        var clip = animationClips[index];
                        animation.AddClip(clip,clip.name);
                        if (index < 1) {
                            animation.clip = clip;
                        }
                    }
                    animation.Play();
                }
            }
#endif // UNITY_ANIMATION
        }
    }
}
