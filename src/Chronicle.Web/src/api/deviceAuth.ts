import client from './client'

export interface DeviceAuthInfoDto {
  displayCode: string
  deviceName: string | null
  status: string
  expiresAt: string
}

export interface PollDeviceAuthResponseDto {
  status: string
  apiKey: string | null
}

export async function getDeviceAuthInfo(code: string): Promise<DeviceAuthInfoDto> {
  const res = await client.get<{ data: DeviceAuthInfoDto }>(`/auth/device/${code}`)
  return res.data.data
}

export async function approveDevice(code: string): Promise<void> {
  await client.post(`/auth/device/${code}/approve`)
}

export async function denyDevice(code: string): Promise<void> {
  await client.post(`/auth/device/${code}/deny`)
}
