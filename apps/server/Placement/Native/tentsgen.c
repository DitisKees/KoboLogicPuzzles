#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>

#include "puzzles.h"

int main(int argc, char **argv)
{
    if (argc != 3 || !argv[1][0] || argv[1][strspn(argv[1], "0123456789")] ||
        strlen(argv[1]) != 2 ||
        !argv[2][0] || strlen(argv[2]) > 64 || argv[2][strspn(argv[2], "0123456789abcdef")]) {
        fputs("Usage: tentsgen SIZE HEX_SEED\n", stderr);
        return 2;
    }
    int size = atoi(argv[1]);
    if (size < 25 || size > 50 || size % 5)
        return 2;

    struct rlimit memory = {128UL * 1024 * 1024, 128UL * 1024 * 1024};
    struct rlimit core = {0, 0};
    if (setrlimit(RLIMIT_AS, &memory) || setrlimit(RLIMIT_CORE, &core))
        fatal("Could not bound native Tents resources.");

    char encoded[32];
    snprintf(encoded, sizeof encoded, "%dx%ddr", size, size);
    game_params *params = thegame.default_params();
    thegame.decode_params(params, encoded);
    if (thegame.validate_params(params, true))
        fatal("Invalid Tents parameters.");
    random_state *random = random_new(argv[2], strlen(argv[2]));
    char *solution = NULL;
    char *description = thegame.new_desc(params, random, &solution, false);
    if (!description || !solution || thegame.validate_desc(params, description))
        fatal("Tents returned an invalid candidate.");
    printf("%s\n%s\n", description, solution);
    sfree(solution);
    sfree(description);
    random_free(random);
    thegame.free_params(params);
    return 0;
}
