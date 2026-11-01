import test from 'node:test'
import assert from 'node:assert/strict'

import {
  PER_APP_GAMEPAD_MODES,
  normalizePerAppGamepadMode,
} from '../utils/gamepadModes.js'

test('per-app gamepad choices include DualSense and Switch Pro', () => {
  assert.deepEqual(
    PER_APP_GAMEPAD_MODES.map(({ value }) => value),
    ['', 'auto', 'x360', 'ds4', 'ds5', 'switch'],
  )
})

test('per-app Switch Pro selection survives normalization', () => {
  assert.equal(normalizePerAppGamepadMode('switch'), 'switch')
  assert.equal(normalizePerAppGamepadMode('xone'), '')
})
