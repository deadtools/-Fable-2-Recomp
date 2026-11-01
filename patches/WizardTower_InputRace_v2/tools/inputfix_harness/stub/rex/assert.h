#pragma once
#define static_assert_size(T, S) static_assert(sizeof(T) == (S), #T)
