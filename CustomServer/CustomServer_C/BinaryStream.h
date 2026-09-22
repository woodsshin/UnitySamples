// BinaryStream.h
//
// .NET의 BinaryReader/BinaryWriter가 기록하는 것과 정확히 동일한 와이어
// 포맷을 재현하는 최소한의 바이너리 리더/라이터: 고정 폭 리틀 엔디안
// 정수와 IEEE-754 부동소수점/배정도, 패딩 없음. C#의 BinaryWriter.Write가
// bool에 대해 단일 바이트(0 또는 1)를 쓰는 것과 동일하게 여기서도 맞춤.
//
// x86/x64에서 빌드하며 이는 리틀 엔디안이므로 네이티브 타입을 그대로
// memcpy해도 무방하지만, 이 클래스는 동작이 호스트 엔디안에 암묵적으로
// 의존하지 않도록 바이트 단위 스왑을 명시적으로 수행함.
#pragma once

#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <vector>

namespace proto
{
    class BinaryWriter
    {
    public:
        void WriteByte(uint8_t v) { buf_.push_back(v); }

        void WriteBool(bool v) { buf_.push_back(v ? 1 : 0); }

        void WriteUInt16(uint16_t v)
        {
            buf_.push_back(static_cast<uint8_t>(v & 0xFF));
            buf_.push_back(static_cast<uint8_t>((v >> 8) & 0xFF));
        }

        void WriteInt32(int32_t v)
        {
            uint32_t u = static_cast<uint32_t>(v);
            for (int i = 0; i < 4; ++i)
                buf_.push_back(static_cast<uint8_t>((u >> (8 * i)) & 0xFF));
        }

        void WriteInt64(int64_t v)
        {
            uint64_t u = static_cast<uint64_t>(v);
            for (int i = 0; i < 8; ++i)
                buf_.push_back(static_cast<uint8_t>((u >> (8 * i)) & 0xFF));
        }

        void WriteSingle(float v)
        {
            static_assert(sizeof(float) == 4, "32비트 float를 기대함");
            uint32_t bits;
            std::memcpy(&bits, &v, 4);
            WriteUInt32Raw(bits);
        }

        const uint8_t* Data() const { return buf_.data(); }
        size_t Size() const { return buf_.size(); }
        const std::vector<uint8_t>& Buffer() const { return buf_; }

    private:
        void WriteUInt32Raw(uint32_t u)
        {
            for (int i = 0; i < 4; ++i)
                buf_.push_back(static_cast<uint8_t>((u >> (8 * i)) & 0xFF));
        }

        std::vector<uint8_t> buf_;
    };

    class BinaryReader
    {
    public:
        BinaryReader(const uint8_t* data, size_t size) : data_(data), size_(size), pos_(0) {}

        bool HasAtLeast(size_t n) const { return pos_ + n <= size_; }

        uint8_t ReadByte()
        {
            RequireBytes(1);
            return data_[pos_++];
        }

        bool ReadBool()
        {
            RequireBytes(1);
            return data_[pos_++] != 0;
        }

        uint16_t ReadUInt16()
        {
            RequireBytes(2);
            uint16_t v = static_cast<uint16_t>(data_[pos_]) |
                (static_cast<uint16_t>(data_[pos_ + 1]) << 8);
            pos_ += 2;
            return v;
        }

        int32_t ReadInt32()
        {
            RequireBytes(4);
            uint32_t v = static_cast<uint32_t>(data_[pos_]) |
                (static_cast<uint32_t>(data_[pos_ + 1]) << 8) |
                (static_cast<uint32_t>(data_[pos_ + 2]) << 16) |
                (static_cast<uint32_t>(data_[pos_ + 3]) << 24);
            pos_ += 4;
            return static_cast<int32_t>(v);
        }

        int64_t ReadInt64()
        {
            RequireBytes(8);
            uint64_t v = 0;
            for (int i = 0; i < 8; ++i)
                v |= static_cast<uint64_t>(data_[pos_ + i]) << (8 * i);
            pos_ += 8;
            return static_cast<int64_t>(v);
        }

        float ReadSingle()
        {
            RequireBytes(4);
            uint32_t bits = static_cast<uint32_t>(data_[pos_]) |
                (static_cast<uint32_t>(data_[pos_ + 1]) << 8) |
                (static_cast<uint32_t>(data_[pos_ + 2]) << 16) |
                (static_cast<uint32_t>(data_[pos_ + 3]) << 24);
            pos_ += 4;
            float f;
            std::memcpy(&f, &bits, 4);
            return f;
        }

        size_t Remaining() const { return size_ - pos_; }

    private:
        void RequireBytes(size_t n) const
        {
            if (pos_ + n > size_)
                throw std::runtime_error("BinaryReader: 패킷이 잘렸음(truncated)");
        }

        const uint8_t* data_;
        size_t size_;
        size_t pos_;
    };
}