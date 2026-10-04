using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    // The fixture has no player registration; therefore it must not unregister a live rig.
    public sealed class DesktopClickTestInput : BasisDesktopEye
    {
        public new void OnDestroy() { }
    }

    public sealed class TouchClickTestInput : BasisTouchInputDevice
    {
        public new void OnDestroy() { }
    }

    public sealed class BasisDesktopClickEdgeTests
    {
        private Mouse mouse;
        private InputAction action, previousAction;
        private EventSystem previousEventSystem;
        private GameObject eventRoot, canvasRoot, inputRoot;
        private BasisInput input;
        private BasisUIRaycastProcess process;
        private int clicks;

        [SetUp]
        public void Setup()
        {
            previousAction = BasisLocalInputActions.LeftMousePressed;
            previousEventSystem = EventSystem.current;
            // Test the native PlayMode input path without resetting project-wide action assets.
            mouse = InputSystem.AddDevice<Mouse>("BasisClickTestMouse");
            action = new InputAction(type: InputActionType.Button, binding: mouse.leftButton.path);
            action.Enable();
            Assert.That(action.controls.Count, Is.EqualTo(1));
            Assert.That(action.controls[0], Is.SameAs(mouse.leftButton));
            BasisLocalInputActions.LeftMousePressed = action;
            eventRoot = new GameObject("Click edge event system", typeof(EventSystem));
            var eventSystem = eventRoot.GetComponent<EventSystem>();
            EventSystem.current = eventSystem;
            canvasRoot = new GameObject("Click edge canvas", typeof(Canvas));
            var target = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            target.transform.SetParent(canvasRoot.transform, false);
            target.GetComponent<Button>().onClick.AddListener(() => clicks++);
            inputRoot = new GameObject("Desktop click fixture");
            input = inputRoot.AddComponent<DesktopClickTestInput>();
            input.HasRaycaster = true;
            input.BasisUIRaycast = new BasisUIRaycast
            {
                CurrentEventData = new BasisPointerEventData(eventSystem),
                WasCorrectLayer = true,
                HadRaycastUITarget = true,
                SortedGraphics = new List<BasisRaycastUIHitData>
                {
                    new BasisRaycastUIHitData(target.GetComponent<Image>(), Vector3.zero, Vector2.zero, 1, 0)
                },
                SortedRays = new List<RaycastResult> { new RaycastResult { gameObject = target } }
            };
            process = new BasisUIRaycastProcess { Inputs = new List<BasisInput> { input } };
            clicks = 0;
        }

        [TearDown]
        public void TearDown()
        {
            BasisLocalInputActions.LeftMousePressed = previousAction;
            action?.Dispose();
            if (mouse != null) InputSystem.RemoveDevice(mouse);
            Object.DestroyImmediate(inputRoot);
            Object.DestroyImmediate(canvasRoot);
            Object.DestroyImmediate(eventRoot);
            if (previousEventSystem != null) EventSystem.current = previousEventSystem;
        }

        [Test]
        public void PressAndReleaseInOneInputUpdateClicksExactlyOnceWithoutAStuckPress()
        {
            InputSystem.QueueStateEvent(mouse, new MouseState().WithButton(MouseButton.Left));
            InputSystem.QueueStateEvent(mouse, new MouseState());
            InputSystem.Update();
            Assert.That(action.ReadValue<float>(), Is.Zero, "The final held state loses this fast click.");
            Assert.That(action.WasPressedThisFrame(), Is.True);
            Assert.That(action.WasReleasedThisFrame(), Is.True);
            process.Simulate();
            Assert.That(clicks, Is.EqualTo(1));
            Assert.That(input.BasisUIRaycast.CurrentEventData.WasLastDown, Is.False);
            Assert.That(input.BasisUIRaycast.CurrentEventData.pointerPress, Is.Null);
            process.Simulate();
            Assert.That(clicks, Is.EqualTo(1), "A second UI pass in the same frame must not replay the edge.");
        }

        [Test]
        public void HeldPressStaysPressedUntilItsRelease()
        {
            InputSystem.QueueStateEvent(mouse, new MouseState().WithButton(MouseButton.Left));
            InputSystem.Update();
            process.Simulate();
            Assert.That(clicks, Is.Zero);
            Assert.That(input.BasisUIRaycast.CurrentEventData.WasLastDown, Is.True);
            InputSystem.Update();
            process.Simulate();
            Assert.That(clicks, Is.Zero);
            Assert.That(input.BasisUIRaycast.CurrentEventData.WasLastDown, Is.True);
            InputSystem.QueueStateEvent(mouse, new MouseState());
            InputSystem.Update();
            process.Simulate();
            Assert.That(clicks, Is.EqualTo(1));
            Assert.That(input.BasisUIRaycast.CurrentEventData.WasLastDown, Is.False);
        }
        [Test]
        public void NonDesktopPointerIgnoresMouseEdgesAndKeepsAnalogHysteresis()
        {
            var raycast = input.BasisUIRaycast;
            Object.DestroyImmediate(inputRoot);
            inputRoot = new GameObject("Touch click fixture");
            input = inputRoot.AddComponent<TouchClickTestInput>();
            input.HasRaycaster = true; input.BasisUIRaycast = raycast;
            process.Inputs = new List<BasisInput> { input };
            InputSystem.QueueStateEvent(mouse, new MouseState().WithButton(MouseButton.Left));
            InputSystem.QueueStateEvent(mouse, new MouseState());
            InputSystem.Update();
            process.Simulate();
            Assert.That(clicks, Is.Zero);
            Assert.That(raycast.CurrentEventData.WasLastDown, Is.False);
            input.CurrentInputState.Trigger = 1;
            process.Simulate();
            Assert.That(raycast.CurrentEventData.WasLastDown, Is.True);
            input.CurrentInputState.Trigger = (BasisSettingsDefaults.UIClickPressThreshold.RawValue +
                Mathf.Min(BasisSettingsDefaults.UIClickReleaseThreshold.RawValue, BasisSettingsDefaults.UIClickPressThreshold.RawValue)) / 2;
            process.Simulate();
            Assert.That(raycast.CurrentEventData.WasLastDown, Is.True);
            Assert.That(clicks, Is.Zero);
            input.CurrentInputState.Trigger = 0;
            process.Simulate();
            Assert.That(clicks, Is.EqualTo(1));
        }

    }
}
