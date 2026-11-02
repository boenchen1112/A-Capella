#pragma once
// Exported C-ABI symbol marker: __declspec(dllexport) on Windows, default symbol visibility
// elsewhere (the macOS build hides everything else).
#if defined(_WIN32)
#define ACA_EXPORT __declspec(dllexport)
#else
#define ACA_EXPORT __attribute__((visibility("default")))
#endif
