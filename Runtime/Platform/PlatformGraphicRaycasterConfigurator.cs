using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public abstract class PlatformGraphicRaycasterConfigurator : MonoBehaviour
    {
        public abstract Type RaycasterType { get; }

        public virtual bool ReplacesStandardGraphicRaycaster => true;

        public virtual bool AppliesTo(Canvas canvas)
        {
            return canvas != null;
        }

        protected virtual void ConfigureRaycaster(
            Canvas canvas,
            BaseRaycaster raycaster)
        {
        }

        internal Type GetValidatedRaycasterType()
        {
            var type = RaycasterType;
            if (type == null)
            {
                throw new InvalidOperationException(
                    GetType().Name + " returned a null RaycasterType.");
            }

            if (type.IsAbstract ||
                !typeof(BaseRaycaster).IsAssignableFrom(type))
            {
                throw new InvalidOperationException(
                    "Platform raycaster type '" + type.FullName +
                    "' must be a non-abstract BaseRaycaster component.");
            }

            return type;
        }

        internal void EnsureConfigured(Canvas canvas, Type raycasterType)
        {
            if (canvas == null || !AppliesTo(canvas))
            {
                return;
            }

            if (ReplacesStandardGraphicRaycaster &&
                raycasterType != typeof(GraphicRaycaster))
            {
                RemoveStandardGraphicRaycasters(canvas);
            }

            var raycaster =
                canvas.GetComponent(raycasterType) as BaseRaycaster;
            if (raycaster == null)
            {
                raycaster =
                    (BaseRaycaster)canvas.gameObject.AddComponent(
                        raycasterType);
            }

            ConfigureRaycaster(canvas, raycaster);
        }

        private static void RemoveStandardGraphicRaycasters(Canvas canvas)
        {
            var raycasters = canvas.GetComponents<GraphicRaycaster>();
            for (var index = 0; index < raycasters.Length; index++)
            {
                var raycaster = raycasters[index];
                if (raycaster == null ||
                    raycaster.GetType() != typeof(GraphicRaycaster))
                {
                    continue;
                }

                raycaster.enabled = false;
                if (Application.isPlaying)
                {
                    Object.Destroy(raycaster);
                }
                else
                {
                    Object.DestroyImmediate(raycaster);
                }
            }
        }
    }
}
