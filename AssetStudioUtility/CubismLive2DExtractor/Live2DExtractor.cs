////
// Based on UnityLive2DExtractorMod by aelurum
// https://github.com/aelurum/UnityLive2DExtractor
//
// Original version - by Perfare
// https://github.com/Perfare/UnityLive2DExtractor
////

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CubismLive2DExtractor
{
    public static class Live2DExtractor
    {
        public static void ExtractLive2D(IGrouping<string, AssetStudio.Object> assets, string destPath, string modelName, AssemblyLoader assemblyLoader)
        {            
            var destTexturePath = Path.Combine(destPath, "textures") + Path.DirectorySeparatorChar;
            var destMotionPath = Path.Combine(destPath, "motions") + Path.DirectorySeparatorChar;
            var destExpressionPath = Path.Combine(destPath, "expressions") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(destPath);
            Directory.CreateDirectory(destTexturePath);

            var monoBehaviours = new List<MonoBehaviour>();
            var texture2Ds = new List<Texture2D>();
            var gameObjects = new List<GameObject>();
            var animationClips = new List<AnimationClip>();

            foreach (var asset in assets)
            {
                switch (asset)
                {
                    case MonoBehaviour m_MonoBehaviour:
                        monoBehaviours.Add(m_MonoBehaviour);
                        break;
                    case Texture2D m_Texture2D:
                        texture2Ds.Add(m_Texture2D);
                        break;
                    case GameObject m_GameObject:
                        gameObjects.Add(m_GameObject);
                        break;
                    case AnimationClip m_AnimationClip:
                        animationClips.Add(m_AnimationClip);
                        break;
                }
            }

            //physics
            var physics = monoBehaviours.FirstOrDefault(x =>
            {
                if (x.m_Script.TryGet(out var m_Script))
                {
                    return m_Script.m_ClassName == "CubismPhysicsController";
                }
                return false;
            });
            if (physics != null)
            {
                try
                {
                    var buff = ParsePhysics(physics, assemblyLoader);
                    File.WriteAllText($"{destPath}{modelName}.physics3.json", buff);
                }
                catch (Exception e)
                {
                    Logger.Warning($"Error in parsing physics data: {e.Message}");
                    physics = null;
                }
            }

            //moc
            var moc = monoBehaviours.First(x =>
            {
                if (x.m_Script.TryGet(out var m_Script))
                {
                    return m_Script.m_ClassName == "CubismMoc";
                }
                return false;
            });
            File.WriteAllBytes($"{destPath}{modelName}.moc3", ParseMoc(moc));

            //texture
            var textures = new SortedSet<string>();
            foreach (var texture2D in texture2Ds)
            {
                using (var image = texture2D.ConvertToImage(flip: true))
                {
                    textures.Add($"textures/{texture2D.m_Name}.png");
                    using (var file = File.OpenWrite($"{destTexturePath}{texture2D.m_Name}.png"))
                    {
                        image.WriteToStream(file, ImageFormat.Png);
                    }
                }
            }

            //motion
            var motions = new SortedDictionary<string, JArray>();

            if (gameObjects.Count > 0)
            {
                var rootTransform = gameObjects[0].m_Transform;
                while (rootTransform.m_Father.TryGet(out var m_Father))
                {
                    rootTransform = m_Father;
                }
                rootTransform.m_GameObject.TryGet(out var rootGameObject);
                var converter = new CubismMotion3Converter(rootGameObject, animationClips.ToArray());
                if (converter.AnimationList.Count > 0)
                {
                    Directory.CreateDirectory(destMotionPath);
                }
                foreach (ImportedKeyframedAnimation animation in converter.AnimationList)
                {
                    var json = new CubismMotion3Json
                    {
                        Version = 3,
                        Meta = new CubismMotion3Json.SerializableMeta
                        {
                            Duration = animation.Duration,
                            Fps = animation.SampleRate,
                            Loop = true,
                            AreBeziersRestricted = true,
                            CurveCount = animation.TrackList.Count,
                            UserDataCount = animation.Events.Count
                        },
                        Curves = new CubismMotion3Json.SerializableCurve[animation.TrackList.Count]
                    };
                    int totalSegmentCount = 1;
                    int totalPointCount = 1;
                    for (int i = 0; i < animation.TrackList.Count; i++)
                    {
                        var track = animation.TrackList[i];
                        json.Curves[i] = new CubismMotion3Json.SerializableCurve
                        {
                            Target = track.Target,
                            Id = track.Name,
                            Segments = new List<float> { 0f, track.Curve[0].value }
                        };
                        for (var j = 1; j < track.Curve.Count; j++)
                        {
                            var curve = track.Curve[j];
                            var preCurve = track.Curve[j - 1];
                            if (Math.Abs(curve.time - preCurve.time - 0.01f) < 0.0001f) //InverseSteppedSegment
                            {
                                var nextCurve = track.Curve[j + 1];
                                if (nextCurve.value == curve.value)
                                {
                                    json.Curves[i].Segments.Add(3f);
                                    json.Curves[i].Segments.Add(nextCurve.time);
                                    json.Curves[i].Segments.Add(nextCurve.value);
                                    j += 1;
                                    totalPointCount += 1;
                                    totalSegmentCount++;
                                    continue;
                                }
                            }
                            if (float.IsPositiveInfinity(curve.inSlope)) //SteppedSegment
                            {
                                json.Curves[i].Segments.Add(2f);
                                json.Curves[i].Segments.Add(curve.time);
                                json.Curves[i].Segments.Add(curve.value);
                                totalPointCount += 1;
                            }
                            else if (preCurve.outSlope == 0f && Math.Abs(curve.inSlope) < 0.0001f) //LinearSegment
                            {
                                json.Curves[i].Segments.Add(0f);
                                json.Curves[i].Segments.Add(curve.time);
                                json.Curves[i].Segments.Add(curve.value);
                                totalPointCount += 1;
                            }
                            else //BezierSegment
                            {
                                var tangentLength = (curve.time - preCurve.time) / 3f;
                                json.Curves[i].Segments.Add(1f);
                                json.Curves[i].Segments.Add(preCurve.time + tangentLength);
                                json.Curves[i].Segments.Add(preCurve.outSlope * tangentLength + preCurve.value);
                                json.Curves[i].Segments.Add(curve.time - tangentLength);
                                json.Curves[i].Segments.Add(curve.value - curve.inSlope * tangentLength);
                                json.Curves[i].Segments.Add(curve.time);
                                json.Curves[i].Segments.Add(curve.value);
                                totalPointCount += 3;
                            }
                            totalSegmentCount++;
                        }
                    }
                    json.Meta.TotalSegmentCount = totalSegmentCount;
                    json.Meta.TotalPointCount = totalPointCount;

                    json.UserData = new CubismMotion3Json.SerializableUserData[animation.Events.Count];
                    var totalUserDataSize = 0;
                    for (var i = 0; i < animation.Events.Count; i++)
                    {
                        var @event = animation.Events[i];
                        json.UserData[i] = new CubismMotion3Json.SerializableUserData
                        {
                            Time = @event.time,
                            Value = @event.value
                        };
                        totalUserDataSize += @event.value.Length;
                    }
                    json.Meta.TotalUserDataSize = totalUserDataSize;

                    var motionPath = new JObject(new JProperty("File", $"motions/{animation.Name}.motion3.json"));
                    motions.Add(animation.Name, new JArray(motionPath));
                    File.WriteAllText($"{destMotionPath}{animation.Name}.motion3.json", JsonConvert.SerializeObject(json, Formatting.Indented, new MyJsonConverter()));
                }
            }

            //expression
            var expressions = new JArray();
            var monoBehaviourArray = monoBehaviours.Where(x => x.m_Name.EndsWith(".exp3")).ToArray();
            if (monoBehaviourArray.Length > 0)
            {
                Directory.CreateDirectory(destExpressionPath);
            }
            foreach (var monoBehaviour in monoBehaviourArray)
            {
                var fullName = monoBehaviour.m_Name;
                var expressionName = fullName.Replace(".exp3", "");
                var expressionObj = monoBehaviour.ToType();
                if (expressionObj == null)
                {
                    var m_Type = monoBehaviour.ConvertToTypeTree(assemblyLoader);
                    expressionObj = monoBehaviour.ToType(m_Type);
                    if (expressionObj == null)
                    {
                        Logger.Warning($"Expression \"{expressionName}\" is not readable.");
                        continue;
                    }
                }
                var expression = JsonConvert.DeserializeObject<CubismExpression3Json>(JsonConvert.SerializeObject(expressionObj));

                expressions.Add(new JObject
                    {
                        { "Name", expressionName },
                        { "File", $"expressions/{fullName}.json" }
                    });
                File.WriteAllText($"{destExpressionPath}{fullName}.json", JsonConvert.SerializeObject(expression, Formatting.Indented));
            }

            //model
            var groups = new List<CubismModel3Json.SerializableGroup>();

            var eyeBlinkParameters = monoBehaviours.Where(x =>
            {
                x.m_Script.TryGet(out var m_Script);
                return m_Script?.m_ClassName == "CubismEyeBlinkParameter";
            }).Select(x =>
            {
                x.m_GameObject.TryGet(out var m_GameObject);
                return m_GameObject?.m_Name;
            }).ToHashSet();
            if (eyeBlinkParameters.Count == 0)
            {
                eyeBlinkParameters = gameObjects.Where(x =>
                {
                    return x.m_Name.ToLower().Contains("eye")
                    && x.m_Name.ToLower().Contains("open")
                    && (x.m_Name.ToLower().Contains('l') || x.m_Name.ToLower().Contains('r'));
                }).Select(x => x.m_Name).ToHashSet();
            }
            groups.Add(new CubismModel3Json.SerializableGroup
            {
                Target = "Parameter",
                Name = "EyeBlink",
                Ids = eyeBlinkParameters.ToArray()
            });

            var lipSyncParameters = monoBehaviours.Where(x =>
            {
                x.m_Script.TryGet(out var m_Script);
                return m_Script?.m_ClassName == "CubismMouthParameter";
            }).Select(x =>
            {
                x.m_GameObject.TryGet(out var m_GameObject);
                return m_GameObject?.m_Name;
            }).ToHashSet();
            if (lipSyncParameters.Count == 0)
            {
                lipSyncParameters = gameObjects.Where(x =>
                {
                    return x.m_Name.ToLower().Contains("mouth")
                    && x.m_Name.ToLower().Contains("open")
                    && x.m_Name.ToLower().Contains('y');
                }).Select(x => x.m_Name).ToHashSet();
            }
            groups.Add(new CubismModel3Json.SerializableGroup
            {
                Target = "Parameter",
                Name = "LipSync",
                Ids = lipSyncParameters.ToArray()
            });

            var model3 = new CubismModel3Json
            {
                Version = 3,
                Name = modelName,
                FileReferences = new CubismModel3Json.SerializableFileReferences
                {
                    Moc = $"{modelName}.moc3",
                    Textures = textures.ToArray(),
                    Motions = JObject.FromObject(motions),
                    Expressions = expressions,
                },
                Groups = groups.ToArray()
            };
            if (physics != null)
            {
                model3.FileReferences.Physics = $"{modelName}.physics3.json";
            }
            File.WriteAllText($"{destPath}{modelName}.model3.json", JsonConvert.SerializeObject(model3, Formatting.Indented));
        }

        private static string ParsePhysics(MonoBehaviour physics, AssemblyLoader assemblyLoader)
        {
            var physicsObj = physics.ToType();
            if (physicsObj == null)
            {
                var m_Type = physics.ConvertToTypeTree(assemblyLoader);
                physicsObj = physics.ToType(m_Type);
                if (physicsObj == null)
                {
                    throw new Exception("MonoBehaviour is not readable.");
                }
            }
            var cubismPhysicsRig = JsonConvert.DeserializeObject<CubismPhysics>(JsonConvert.SerializeObject(physicsObj))._rig;

            var physicsSettings = new CubismPhysics3Json.SerializablePhysicsSettings[cubismPhysicsRig.SubRigs.Length];
            for (int i = 0; i < physicsSettings.Length; i++)
            {
                var subRigs = cubismPhysicsRig.SubRigs[i];
                physicsSettings[i] = new CubismPhysics3Json.SerializablePhysicsSettings
                {
                    Id = $"PhysicsSetting{i + 1}",
                    Input = new CubismPhysics3Json.SerializableInput[subRigs.Input.Length],
                    Output = new CubismPhysics3Json.SerializableOutput[subRigs.Output.Length],
                    Vertices = new CubismPhysics3Json.SerializableVertex[subRigs.Particles.Length],
                    Normalization = new CubismPhysics3Json.SerializableNormalization
                    {
                        Position = new CubismPhysics3Json.SerializableNormalizationValue
                        {
                            Minimum = subRigs.Normalization.Position.Minimum,
                            Default = subRigs.Normalization.Position.Default,
                            Maximum = subRigs.Normalization.Position.Maximum
                        },
                        Angle = new CubismPhysics3Json.SerializableNormalizationValue
                        {
                            Minimum = subRigs.Normalization.Angle.Minimum,
                            Default = subRigs.Normalization.Angle.Default,
                            Maximum = subRigs.Normalization.Angle.Maximum
                        }
                    }
                };
                for (int j = 0; j < subRigs.Input.Length; j++)
                {
                    var input = subRigs.Input[j];
                    physicsSettings[i].Input[j] = new CubismPhysics3Json.SerializableInput
                    {
                        Source = new CubismPhysics3Json.SerializableParameter
                        {
                            Target = "Parameter", //同名GameObject父节点的名称
                            Id = input.SourceId
                        },
                        Weight = input.Weight,
                        Type = Enum.GetName(typeof(CubismPhysicsSourceComponent), input.SourceComponent),
                        Reflect = input.IsInverted
                    };
                }
                for (int j = 0; j < subRigs.Output.Length; j++)
                {
                    var output = subRigs.Output[j];
                    physicsSettings[i].Output[j] = new CubismPhysics3Json.SerializableOutput
                    {
                        Destination = new CubismPhysics3Json.SerializableParameter
                        {
                            Target = "Parameter", //同名GameObject父节点的名称
                            Id = output.DestinationId
                        },
                        VertexIndex = output.ParticleIndex,
                        Scale = output.AngleScale,
                        Weight = output.Weight,
                        Type = Enum.GetName(typeof(CubismPhysicsSourceComponent), output.SourceComponent),
                        Reflect = output.IsInverted
                    };
                }
                for (int j = 0; j < subRigs.Particles.Length; j++)
                {
                    var particles = subRigs.Particles[j];
                    physicsSettings[i].Vertices[j] = new CubismPhysics3Json.SerializableVertex
                    {
                        Position = particles.InitialPosition,
                        Mobility = particles.Mobility,
                        Delay = particles.Delay,
                        Acceleration = particles.Acceleration,
                        Radius = particles.Radius
                    };
                }
            }
            var physicsDictionary = new CubismPhysics3Json.SerializablePhysicsDictionary[physicsSettings.Length];
            for (int i = 0; i < physicsSettings.Length; i++)
            {
                physicsDictionary[i] = new CubismPhysics3Json.SerializablePhysicsDictionary
                {
                    Id = $"PhysicsSetting{i + 1}",
                    Name = $"Dummy{i + 1}"
                };
            }
            var physicsJson = new CubismPhysics3Json
            {
                Version = 3,
                Meta = new CubismPhysics3Json.SerializableMeta
                {
                    PhysicsSettingCount = cubismPhysicsRig.SubRigs.Length,
                    TotalInputCount = cubismPhysicsRig.SubRigs.Sum(x => x.Input.Length),
                    TotalOutputCount = cubismPhysicsRig.SubRigs.Sum(x => x.Output.Length),
                    VertexCount = cubismPhysicsRig.SubRigs.Sum(x => x.Particles.Length),
                    EffectiveForces = new CubismPhysics3Json.SerializableEffectiveForces
                    {
                        Gravity = cubismPhysicsRig.Gravity,
                        Wind = cubismPhysicsRig.Wind
                    },
                    PhysicsDictionary = physicsDictionary
                },
                PhysicsSettings = physicsSettings
            };
            return JsonConvert.SerializeObject(physicsJson, Formatting.Indented, new MyJsonConverter2());
        }

        private static byte[] ParseMoc(MonoBehaviour moc)
        {
            var reader = moc.reader;
            reader.Reset();
            reader.Position += 28; //PPtr<GameObject> m_GameObject, m_Enabled, PPtr<MonoScript>
            reader.ReadAlignedString(); //m_Name
            return reader.ReadBytes(reader.ReadInt32());
        }
    }
}
