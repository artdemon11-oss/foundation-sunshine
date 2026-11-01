using System.Buffers.Binary;
using System.Diagnostics;
using HIDMaestro;

namespace Sunshine.Ds5Sidecar;

/// <summary>Owns one HIDMaestro Switch Pro Controller (057E:2009) for a Moonlight gamepad.</summary>
internal sealed class SwitchProSession : IControllerSession
{
    internal const string ProfileId = "switch-pro";

    private const uint DpadUp = 0x0001;
    private const uint DpadDown = 0x0002;
    private const uint DpadLeft = 0x0004;
    private const uint DpadRight = 0x0008;
    private const uint Start = 0x0010;
    private const uint Back = 0x0020;
    private const uint LeftStick = 0x0040;
    private const uint RightStick = 0x0080;
    private const uint LeftBumper = 0x0100;
    private const uint RightBumper = 0x0200;
    private const uint Guide = 0x0400;
    private const uint A = 0x1000;
    private const uint B = 0x2000;
    private const uint X = 0x4000;
    private const uint Y = 0x8000;
    private const uint Touchpad = 0x100000;
    private const uint Misc = 0x200000;

    // HIDMaestro v1.12.0 packs report 0x30 from descriptor button indices,
    // not from semantic HMButton flags (SwitchProPacker.ButtonsByte*).
    internal const int IndexB = 0;
    internal const int IndexA = 1;
    internal const int IndexY = 2;
    internal const int IndexX = 3;
    internal const int IndexL = 4;
    internal const int IndexR = 5;
    internal const int IndexZl = 6;
    internal const int IndexZr = 7;
    internal const int IndexMinus = 8;
    internal const int IndexPlus = 9;
    internal const int IndexLeftStick = 10;
    internal const int IndexRightStick = 11;
    internal const int IndexHome = 12;
    internal const int IndexCapture = 13;

    // Core sends ZL/ZR already debounced as 0 or 255; any analog value is split at the midpoint.
    internal const byte TriggerPressedThreshold = 128;

    private readonly object _stateLock = new();
    private readonly object _outputLock = new();
    private readonly HMController _controller;
    private readonly StandardAxisState _axes;
    private readonly Action<Protocol.Message> _emit;
    private readonly RumbleFilter _rumble = new();
    private HMGamepadState _state;
    private int _disposed;

    internal SwitchProSession(byte deviceId,
                              byte clientControllerNumber,
                              HMController controller,
                              HMProfile profile,
                              Action<Protocol.Message> emit)
    {
        DeviceId = deviceId;
        ClientControllerNumber = clientControllerNumber;
        _controller = controller;
        _axes = new StandardAxisState(profile);
        _emit = emit;
        _state = new HMGamepadState { Axes = _axes.Values };

        _controller.OutputDecoded += OnOutputDecoded;
        // Centered sticks and released buttons until the first client input.
        _controller.SubmitState(in _state);
    }

    public byte DeviceId { get; }
    public byte ClientControllerNumber { get; }
    public bool HasAudio => false;

    public void StartDefaultAudioEndpointGuard()
    {
        // Switch Pro has no audio endpoint.
    }

    public void SubmitInput(ReadOnlySpan<byte> payload)
    {
        // Same 20-byte layout as the DualSense session.
        if (payload.Length != 20 || payload[0] != DeviceId)
            throw new InvalidDataException("Invalid input-state payload");

        var buttons = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        var leftX = Axis(BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(12, 2)));
        var leftY = Axis(BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(14, 2)), invert: true);
        var rightX = Axis(BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(16, 2)));
        var rightY = Axis(BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(18, 2)), invert: true);

        lock (_stateLock)
        {
            _state.Buttons = (HMButton)MapButtons(buttons, payload[8], payload[9]);
            _state.Hat = MapHat(buttons);
            _axes.Update(leftX, leftY, rightX, rightY, 0, 0);
            SubmitStateLocked();
        }
    }

    public void SubmitTouch(ReadOnlySpan<byte> payload)
    {
        // A Pro Controller has no touchpad; keep the channel quiet.
    }

    /// <summary>Updates one SDL-frame motion sensor; the SDK converts it to Switch IMU axes.</summary>
    public void SubmitMotion(ReadOnlySpan<byte> payload)
    {
        // id:u8, type:u8, reserved:u16, x/y/z:f32 = 16 bytes
        if (payload.Length != 16 || payload[0] != DeviceId)
            throw new InvalidDataException("Invalid motion payload");
        var type = payload[1];
        var x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)));
        var y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8, 4)));
        var z = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4)));

        lock (_stateLock)
        {
            if (type == 1)
            {
                _state.AccelGX = x / 9.80665f;
                _state.AccelGY = y / 9.80665f;
                _state.AccelGZ = z / 9.80665f;
            }
            else if (type == 2)
            {
                _state.GyroDpsX = x;
                _state.GyroDpsY = y;
                _state.GyroDpsZ = z;
            }
            else
            {
                throw new InvalidDataException("Unsupported motion type");
            }
            SubmitStateLocked();
        }
    }

    public void SubmitBattery(ReadOnlySpan<byte> payload)
    {
        // The v1.12.0 driver overlays its own battery byte on report 0x30.
        if (payload.Length != 4 || payload[0] != DeviceId)
            throw new InvalidDataException("Invalid battery payload");
    }

    private void SubmitStateLocked()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _controller.SubmitState(in _state);
    }

    private void OnOutputDecoded(object? sender, HMOutputDecodedEventArgs output)
    {
        if (!output.CrcValid ||
            !output.Fields.TryGetValue("leftMotor", out var leftValue) || leftValue is not byte left ||
            !output.Fields.TryGetValue("rightMotor", out var rightValue) || rightValue is not byte right)
        {
            return;
        }

        lock (_outputLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_rumble.ShouldSend(left, right, Stopwatch.GetTimestamp()))
                return;
            EmitRumble(left, right);
        }
    }

    private void EmitRumble(byte left, byte right)
    {
        var payload = new byte[6];
        payload[0] = DeviceId;
        payload[1] = ClientControllerNumber;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), (ushort)(left * 257));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), (ushort)(right * 257));
        _emit(new Protocol.Message(Protocol.MessageType.Rumble, 0, payload));
    }

    /// <summary>Maps Moonlight button flags to Switch descriptor indices by physical position.</summary>
    internal static uint MapButtons(uint flags, byte leftTrigger, byte rightTrigger)
    {
        var result = 0u;
        if ((flags & A) != 0) result |= 1u << IndexB;
        if ((flags & B) != 0) result |= 1u << IndexA;
        if ((flags & X) != 0) result |= 1u << IndexY;
        if ((flags & Y) != 0) result |= 1u << IndexX;
        if ((flags & LeftBumper) != 0) result |= 1u << IndexL;
        if ((flags & RightBumper) != 0) result |= 1u << IndexR;
        if (leftTrigger >= TriggerPressedThreshold) result |= 1u << IndexZl;
        if (rightTrigger >= TriggerPressedThreshold) result |= 1u << IndexZr;
        if ((flags & Back) != 0) result |= 1u << IndexMinus;
        if ((flags & Start) != 0) result |= 1u << IndexPlus;
        if ((flags & LeftStick) != 0) result |= 1u << IndexLeftStick;
        if ((flags & RightStick) != 0) result |= 1u << IndexRightStick;
        if ((flags & Guide) != 0) result |= 1u << IndexHome;
        if ((flags & (Misc | Touchpad)) != 0) result |= 1u << IndexCapture;
        return result;
    }

    internal static HMHat MapHat(uint flags)
    {
        var up = (flags & DpadUp) != 0;
        var down = (flags & DpadDown) != 0;
        var left = (flags & DpadLeft) != 0;
        var right = (flags & DpadRight) != 0;
        if (up && right) return HMHat.NorthEast;
        if (down && right) return HMHat.SouthEast;
        if (down && left) return HMHat.SouthWest;
        if (up && left) return HMHat.NorthWest;
        if (up) return HMHat.North;
        if (right) return HMHat.East;
        if (down) return HMHat.South;
        if (left) return HMHat.West;
        return HMHat.None;
    }

    private static float Axis(short value, bool invert = false)
    {
        var normalized = (value - (float)short.MinValue) / ushort.MaxValue;
        return invert ? 1.0f - normalized : normalized;
    }

    /// <summary>Forwards amplitude changes at once and repeats an unchanged level at most every 100 ms.</summary>
    internal sealed class RumbleFilter
    {
        private static readonly long RepeatInterval = Stopwatch.Frequency / 10;
        private bool _sent;
        private byte _left;
        private byte _right;
        private long _lastSent;

        internal bool ShouldSend(byte left, byte right, long timestamp)
        {
            if (_sent && left == _left && right == _right && timestamp - _lastSent < RepeatInterval)
                return false;
            _sent = true;
            _left = left;
            _right = right;
            _lastSent = timestamp;
            return true;
        }

        /// <summary>Reports whether the client may still be vibrating.</summary>
        internal bool Active => _sent && (_left != 0 || _right != 0);
    }

    /// <summary>Stops the client motors and releases the virtual controller.</summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
        }
        _controller.OutputDecoded -= OnOutputDecoded;
        lock (_outputLock)
        {
            if (_rumble.Active)
                EmitRumble(0, 0);
        }
        _controller.Dispose();
    }
}
