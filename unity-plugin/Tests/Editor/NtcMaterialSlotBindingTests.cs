using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ntc.Tests
{
    public class NtcMaterialSlotBindingTests
    {
        GameObject _rendererObject;
        GameObject _controllerObject;
        Material _slot0;
        Material _slot1;
        Material _replacement0;
        Material _replacement1;

        [SetUp]
        public void SetUp()
        {
            Shader shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            string unique = Guid.NewGuid().ToString("N");
            _slot0 = new Material(shader) { name = "Slot0_" + unique };
            _slot1 = new Material(shader) { name = "Slot1_" + unique };
            _replacement0 = new Material(shader) { name = "Replacement0_" + unique };
            _replacement1 = new Material(shader) { name = "Replacement1_" + unique };

            _rendererObject = new GameObject("NTC multi-slot test renderer");
            _rendererObject.AddComponent<MeshFilter>();
            _rendererObject.AddComponent<MeshRenderer>().sharedMaterials = new[] { _slot0, _slot1 };
            _controllerObject = new GameObject("NTC multi-slot test controller");
            _controllerObject.SetActive(false); // avoid play-only initialization coroutines in EditMode
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_controllerObject);
            UnityEngine.Object.DestroyImmediate(_rendererObject);
            UnityEngine.Object.DestroyImmediate(_slot0);
            UnityEngine.Object.DestroyImmediate(_slot1);
            UnityEngine.Object.DestroyImmediate(_replacement0);
            UnityEngine.Object.DestroyImmediate(_replacement1);
        }

        [Test]
        public void AutoScan_GroupsByMaterialReference_AndWritesOnlyTargetSlot()
        {
            _slot1.name = _slot0.name; // distinct objects with the same display name stay separate
            var controller = _controllerObject.AddComponent<Ntc.Unity.NtcSceneController>();
            controller.ScanSceneMaterials();
            Renderer renderer = _rendererObject.GetComponent<Renderer>();

            Ntc.Unity.NtcSceneController.MaterialGroup group0 = FindGroupForRendererAndOriginal(controller, renderer, _slot0);
            Ntc.Unity.NtcSceneController.MaterialGroup group1 = FindGroupForRendererAndOriginal(controller, renderer, _slot1);
            Assert.That(group0, Is.Not.SameAs(group1));

            Assign(group0, _replacement0);
            CollectionAssert.AreEqual(new[] { _replacement0, _slot1 }, renderer.sharedMaterials);

            Assign(group1, _replacement1);
            CollectionAssert.AreEqual(new[] { _replacement0, _replacement1 }, renderer.sharedMaterials);

            Restore(controller, group0);
            CollectionAssert.AreEqual(new[] { _slot0, _replacement1 }, renderer.sharedMaterials);
            Restore(controller, group1);
            CollectionAssert.AreEqual(new[] { _slot0, _slot1 }, renderer.sharedMaterials);
        }

        [Test]
        public void AutoScan_SameMaterialInTwoSlots_UpdatesBothSlots()
        {
            Renderer renderer = _rendererObject.GetComponent<Renderer>();
            renderer.sharedMaterials = new[] { _slot0, _slot0 };
            var controller = _controllerObject.AddComponent<Ntc.Unity.NtcSceneController>();
            controller.ScanSceneMaterials();

            Ntc.Unity.NtcSceneController.MaterialGroup group = controller.groups
                .Single(g => g.renderers != null && g.renderers.Contains(renderer));
            Init(controller, group);
            Assert.That(group.originalMaterials, Has.Length.EqualTo(2));

            Assign(group, _replacement0);
            CollectionAssert.AreEqual(new[] { _replacement0, _replacement0 }, renderer.sharedMaterials);
            Restore(controller, group);
            CollectionAssert.AreEqual(new[] { _slot0, _slot0 }, renderer.sharedMaterials);
        }

        [Test]
        public void LegacyRendererOnlyGroup_RecoversNamedNonZeroSlot()
        {
            var controller = _controllerObject.AddComponent<Ntc.Unity.NtcSceneController>();
            var group = new Ntc.Unity.NtcSceneController.MaterialGroup
            {
                name = _slot1.name,
                renderers = new[] { _rendererObject.GetComponent<Renderer>() }
            };
            Init(controller, group);
            Assign(group, _replacement1);
            CollectionAssert.AreEqual(new[] { _slot0, _replacement1 }, group.renderers[0].sharedMaterials);
            Restore(controller, group);
            CollectionAssert.AreEqual(new[] { _slot0, _slot1 }, group.renderers[0].sharedMaterials);
        }

        static Ntc.Unity.NtcSceneController.MaterialGroup FindGroupForRendererAndOriginal(
            Ntc.Unity.NtcSceneController controller, Renderer renderer, Material original)
        {
            foreach (var group in controller.groups.Where(g => g.renderers != null && g.renderers.Contains(renderer)))
            {
                Init(controller, group);
                if (group.originalMaterials.Length == 1 && group.originalMaterials[0] == original)
                    return group;
            }
            Assert.Fail("Auto-scan did not preserve an exact slot binding for " + original.name);
            return null;
        }

        static void Init(Ntc.Unity.NtcSceneController controller, Ntc.Unity.NtcSceneController.MaterialGroup group)
        {
            typeof(Ntc.Unity.NtcSceneController).GetMethod("InitGroup", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { group });
        }

        static void Assign(Ntc.Unity.NtcSceneController.MaterialGroup group, Material material)
        {
            typeof(Ntc.Unity.NtcSceneController).GetMethod("AssignGroupMaterial", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { group, material });
        }

        static void Restore(Ntc.Unity.NtcSceneController controller, Ntc.Unity.NtcSceneController.MaterialGroup group)
        {
            typeof(Ntc.Unity.NtcSceneController).GetMethod("RestoreOriginalMaterialRefs", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { group });
        }
    }
}
