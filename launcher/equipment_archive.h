#pragma once
#include <filesystem>

std::filesystem::path PrepareEquipmentArchive(const std::filesystem::path& source,
                                               const std::filesystem::path& pack,
                                               const std::filesystem::path& cache);
