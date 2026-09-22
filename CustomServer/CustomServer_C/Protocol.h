// Protocol.h

#pragma once

#include <cstdint>

namespace proto
{
    // CustomServer.PacketType을 미러링 (C# enum : byte).
    enum class PacketType : uint8_t
    {
        JoinRequest = 1,
        JoinResponse = 2,
        ClientInput = 3,
        ServerState = 4,
        Ping = 5,
        Pong = 6,
        MissileState = 7,
        ScoreboardState = 8
    };

    // --- 서버 틱 ---
    constexpr int PORT = 9050;
    constexpr float TICK_RATE = 60.0f; // 60Hz
    constexpr float TIMEOUT_SECONDS = 3.0f;

    // --- 탱크 이동 (CustomServer.cs, CustomClient.cs와 반드시 일치해야 함) ---
    constexpr float MAX_FORWARD_SPEED = 3.0f;
    constexpr float MAX_REVERSE_SPEED = 3.0f;
    constexpr float ACCELERATION = 4.0f;
    constexpr float TURN_SPEED_DEG = 160.0f;

    // --- 미사일 ---
    constexpr float MISSILE_SPEED = 12.0f;
    constexpr float FIRE_COOLDOWN_SECONDS = 0.3f;
    constexpr float MISSILE_RADIUS = 0.06f;
    constexpr float TANK_COLLISION_RADIUS = 0.175f;
    constexpr float MISSILE_MAX_DISTANCE = 14.2222222f;

    // --- 체력 / 리스폰 ---
    constexpr int   MAX_HEALTH = 10;
    constexpr float RESPAWN_DELAY_SECONDS = 3.0f;

    // --- 월드 경계 / 스폰 ---
    constexpr float WORLD_HALF_EXTENT_X = 7.1111111f; // 16:9 절반 너비
    constexpr float WORLD_HALF_EXTENT_Y = 4.0f;        // 절반 높이
    constexpr float SPAWN_RADIUS = 1.2f;
    constexpr int   SPAWN_SLOTS = 16;
}