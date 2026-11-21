cmake_minimum_required(VERSION 3.20)
project(ChatCompleteNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(nativesuggest SHARED NativeSuggest.cpp)
target_compile_definitions(nativesuggest PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(nativesuggest PRIVATE user32)
set_target_properties(nativesuggest PROPERTIES OUTPUT_NAME "NativeSuggest")
