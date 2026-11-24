#define _POSIX_C_SOURCE 200809L

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <time.h>

#include "puzzles.h"

static int bounded_number(const char *text, int min, int max)
{
    if (!text[0] || text[strspn(text, "0123456789")])
        return -1;

    long value = strtol(text, NULL, 10);
    return value >= min && value <= max ? (int)value : -1;
}

static long peak_rss_kib(void)
{
    /* VmHWM measures this executable even when its launcher used more memory. */
    FILE *status = fopen("/proc/self/status", "r");
    if (!status)
        fatal("Failed to open /proc/self/status");

    char line[256];
    long peak = -1;
    while (fgets(line, sizeof line, status)) {
        if (sscanf(line, "VmHWM: %ld", &peak) == 1)
            break;
    }
    fclose(status);
    if (peak < 0)
        fatal("Failed to read peak resident memory");
    return peak;
}

static void limit_resources(void)
{
    const rlim_t memory_bytes = 128UL * 1024 * 1024;
    struct rlimit address_space;
    if (getrlimit(RLIMIT_AS, &address_space) != 0)
        fatal("Failed to read the address space limit");
    if (address_space.rlim_cur > memory_bytes) {
        address_space.rlim_cur = memory_bytes;
        if (setrlimit(RLIMIT_AS, &address_space) != 0)
            fatal("Failed to limit the address space");
    }
    struct rlimit core_size = { 0, 0 };
    if (setrlimit(RLIMIT_CORE, &core_size) != 0)
        fatal("Failed to disable core dumps");
}

int main(int argc, char **argv)
{
    if (argc != 6) {
        fprintf(stderr, "Usage: %s SIZE DENSITY EXPANSION DIFFICULTY HEX_SEED\n", argv[0]);
        return 2;
    }

    int size = bounded_number(argv[1], 20, 50);
    int density = bounded_number(argv[2], 1, 30);
    int expansion = bounded_number(argv[3], 0, 100);
    int difficulty = bounded_number(argv[4], 0, 2);
    size_t seed_length = strlen(argv[5]);
    if (size < 0 || size % 5 || density < 0 || expansion < 0 || difficulty < 0 ||
        !seed_length || seed_length > 64 || argv[5][strspn(argv[5], "0123456789abcdef")]) {
        fputs("Invalid size, density, expansion, difficulty, or seed.\n", stderr);
        return 2;
    }

    limit_resources();
    char encoded[64];
    snprintf(encoded, sizeof encoded, "%dx%di%de%dm2d%d", size, size, density, expansion, difficulty);
    game_params *params = thegame.default_params();
    thegame.decode_params(params, encoded);
    const char *error = thegame.validate_params(params, true);
    if (error) {
        fprintf(stderr, "%s\n", error);
        thegame.free_params(params);
        return 2;
    }

    random_state *random = random_new(argv[5], (int)seed_length);
    char *solution = NULL;
    struct timespec start, end;
    if (clock_gettime(CLOCK_MONOTONIC, &start) != 0)
        fatal("Failed to start the generation timer");
    char *description = thegame.new_desc(params, random, &solution, false);
    if (clock_gettime(CLOCK_MONOTONIC, &end) != 0)
        fatal("Failed to read the generation timer");
    long peak = peak_rss_kib();

    error = thegame.validate_desc(params, description);
    if (error || !solution)
        fatal("Generator returned an invalid candidate: %s", error ? error : "missing solution");

    double milliseconds = (end.tv_sec - start.tv_sec) * 1000.0 + (end.tv_nsec - start.tv_nsec) / 1e6;
    printf("%s\n%s\n%.3f %ld\n", description, solution, milliseconds, peak);
    sfree(solution);
    sfree(description);
    random_free(random);
    thegame.free_params(params);
    return 0;
}
