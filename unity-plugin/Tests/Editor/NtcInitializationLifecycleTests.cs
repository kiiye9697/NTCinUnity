using System.Collections;
using System.Reflection;
using Ntc.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ntc.Tests
{
    public class NtcInitializationLifecycleTests
    {
        [UnityTest]
        public IEnumerator InterruptedInitialization_RestartsAfterObjectOrComponentDisable()
        {
            yield return new EnterPlayMode();
            // Exercise both Unity cancellation behaviors, before allocation and after
            // the scan has allocated compute resources and initialized some groups.
            foreach (bool disableObject in new[] { true, false })
            foreach (int framesBeforeDisable in new[] { 0, 2 })
            {
                var host = new GameObject("NTC interrupted initialization");
                host.SetActive(false);
                var controller = host.AddComponent<NtcSceneController>();
                controller.showPanel = false;
                controller.groups = new NtcSceneController.MaterialGroup[8];
                for (int i = 0; i < controller.groups.Length; i++)
                    controller.groups[i] = new NtcSceneController.MaterialGroup { name = "UnbakedLifecycleCase_" + i };
                try
                {
                    host.SetActive(true);
                    for (int i = 0; i < framesBeforeDisable; i++) yield return null;
                    Assert.That(Read<bool>(controller, "_initializing"), Is.True);
                    Assert.That(controller.IsReady, Is.False);

                    if (disableObject) host.SetActive(false); else controller.enabled = false;
                    Assert.That(Read<bool>(controller, "_initializing"), Is.False);
                    Assert.That(Read<Coroutine>(controller, "_initializeRoutine"), Is.Null);
                    Assert.That(Read<NtcDecompressor>(controller, "_decompressor"), Is.Null);
                    // A disabled component's old coroutine must not keep scanning.
                    yield return null;
                    Assert.That(controller.IsReady, Is.False);

                    if (disableObject) host.SetActive(true); else controller.enabled = true;
                    for (int i = 0; i < 120 && !controller.IsReady; i++) yield return null;
                    Assert.That(controller.IsReady, Is.True, "Initialization did not restart after disable/re-enable.");
                    foreach (var group in controller.groups)
                        Assert.That(group.originalMaterials, Is.Not.Null, "A group was skipped after restart.");
                }
                finally
                {
                    Object.Destroy(host);
                }
                yield return null;
            }
        }

        [UnityTearDown]
        public IEnumerator LeavePlayMode()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        static T Read<T>(NtcSceneController controller, string name)
            => (T)typeof(NtcSceneController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
    }
}
