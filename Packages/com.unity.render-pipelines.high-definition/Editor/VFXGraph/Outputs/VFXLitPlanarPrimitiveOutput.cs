using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.VFX.Block;
using UnityEngine;

namespace UnityEditor.VFX.HDRP
{
    class VFXLitPlanarPrimitiveOutputProvider : VariantProvider
    {
        public override IEnumerable<Variant> GetVariants()
        {
            foreach (var primitive in Enum.GetValues(typeof(VFXPrimitiveType)))
            {
                yield return new Variant(
                    $"Lit Output Particle {primitive}",
                    "Output",
                    typeof(VFXLitPlanarPrimitiveOutput),
                    new[] {new KeyValuePair<string, object>("primitiveType", primitive)});
            }
        }
    }

    [VFXInfo(variantProvider = typeof(VFXLitPlanarPrimitiveOutputProvider))]
    class VFXLitPlanarPrimitiveOutput : VFXAbstractParticleHDRPLitOutput
    {
        public override string name
        {
            get
            {
                return "Output Particle HDRP Lit " + primitiveType.ToString();
            }
        }
        public override string codeGeneratorTemplate { get { return RenderPipeTemplate("VFXParticleLitPlanarPrimitive"); } }
        public override VFXTaskType taskType { get { return VFXPlanarPrimitiveHelper.GetTaskType(primitiveType); } }
        public override bool supportsUV { get { return GetOrRefreshShaderGraphObject() == null; } }
        public sealed override bool implementsMotionVector { get { return true; } }

        [VFXSetting(VFXSettingAttribute.VisibleFlags.InInspector), SerializeField, Tooltip("Specifies what primitive type to use for this output. Triangle outputs have fewer vertices, octagons can be used to conform the geometry closer to the texture to avoid overdraw, and quads are a good middle ground.")]
        protected VFXPrimitiveType primitiveType = VFXPrimitiveType.Quad;

        [VFXSetting(VFXSettingAttribute.VisibleFlags.InInspector), SerializeField, Tooltip("When enabled, a Normal Bending Factor slider becomes available in the output which can be used to adjust the curvature of the normals.")]
        protected bool normalBending = false;

        public class NormalBendingProperties
        {
            [Range(0, 1), Tooltip("Controls the amount by which the normals will be bent, creating a rounder look.")]
            public float normalBendingFactor = 0.1f;
        }

        protected override IEnumerable<VFXPropertyWithValue> inputProperties
        {
            get
            {
                var properties = base.inputProperties;
                if (normalBending)
                    properties = properties.Concat(PropertiesFromType("NormalBendingProperties"));
                if (primitiveType == VFXPrimitiveType.Octagon)
                    properties = properties.Concat(PropertiesFromType(typeof(VFXPlanarPrimitiveHelper.OctagonInputProperties)));
                return properties;
            }
        }

        protected override IEnumerable<VFXNamedExpression> CollectGPUExpressions(IEnumerable<VFXNamedExpression> slotExpressions)
        {
            foreach (var exp in base.CollectGPUExpressions(slotExpressions))
                yield return exp;

            if (normalBending)
                yield return slotExpressions.First(o => o.name == "normalBendingFactor");
            if (primitiveType == VFXPrimitiveType.Octagon)
                yield return slotExpressions.First(o => o.name == "cropFactor");
        }

        public override IEnumerable<string> additionalDefines
        {
            get
            {
                foreach (var d in base.additionalDefines)
                    yield return d;

                if (normalBending)
                    yield return "USE_NORMAL_BENDING";

                yield return "FORCE_NORMAL_VARYING"; // To avoid discrepancy between depth and color pass which could cause glitch with ztest

                yield return VFXPlanarPrimitiveHelper.GetShaderDefine(primitiveType);
            }
        }

        protected override IEnumerable<string> untransferableSettings
        {
            get
            {
                foreach (var setting in base.untransferableSettings)
                {
                    yield return setting;
                }
                yield return "primitiveType";
            }
        }
    }
}
