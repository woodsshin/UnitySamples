// main.cpp
//
// CustomServer.cs의 Program.Main을 미러링함: 서버를 시작하고, stdin의 한 줄 입력을 대기한 뒤 종료함.
//
// CUSTOM_SERVER_RUN_SECONDS는 테스트 전용 우회 수단(원본 C#에는 없음)으로,
// 자동화된 테스트가 stdin을 계속 열어둔 채 대기할 필요 없이 서버를 고정된
// 시간 동안 실행할 수 있게 함. 일반/운영 환경 사용(환경 변수 미설정)은
// 변경 없음: C# 버전과 동일하게 stdin을 대기함.

#define _CRT_SECURE_NO_WARNINGS

#include <chrono>
#include <cstdlib>
#include <iostream>
#include <string>
#include <thread>

#include "CustomServer.h"

int main()
{
    server::CustomServer srv;
    srv.Start();

    if (const char* runSeconds = std::getenv("CUSTOM_SERVER_RUN_SECONDS"))
    {
        int seconds = std::atoi(runSeconds);
        std::cout << "[test mode] running for " << seconds << " seconds..." << std::endl;
        std::this_thread::sleep_for(std::chrono::seconds(seconds));
    }
    else
    {
        std::cout << "Press Enter to stop server..." << std::endl;
        std::string line;
        std::getline(std::cin, line);
    }

    srv.Stop();
    return 0;
}