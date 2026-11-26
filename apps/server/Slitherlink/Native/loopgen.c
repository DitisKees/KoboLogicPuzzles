#define _POSIX_C_SOURCE 200809L

#include <sys/resource.h>
#include "loopy.c"

static int number(const char *text, int min, int max)
{
    if (!*text || text[strspn(text, "0123456789")])
        return -1;
    long value = strtol(text, NULL, 10);
    return value >= min && value <= max ? (int)value : -1;
}

static void limit_resources(void)
{
    struct rlimit limit;
    const rlim_t max_memory = 256UL * 1024 * 1024;
    if (getrlimit(RLIMIT_AS, &limit) != 0)
        fatal("Could not read the address space limit");
    if (limit.rlim_cur > max_memory) {
        limit.rlim_cur = max_memory;
        if (setrlimit(RLIMIT_AS, &limit) != 0)
            fatal("Could not limit the address space");
    }
    limit = (struct rlimit){0, 0};
    if (setrlimit(RLIMIT_CORE, &limit) != 0)
        fatal("Could not disable core dumps");
}

int main(int argc, char **argv)
{
    if (argc != 7 || (strcmp(argv[1], "generate") && strcmp(argv[1], "decode"))) {
        fputs("Usage: loopgen generate|decode WIDTH HEIGHT TILING DIFFICULTY SEED|DESCRIPTION\n", stderr);
        return 2;
    }
    bool generate = !strcmp(argv[1], "generate");
    int width = number(argv[2], 1, 100), height = number(argv[3], 1, 100);
    int type = number(argv[4], 0, NUM_GRID_TYPES - 1);
    int difficulty = number(argv[5], 0, DIFF_MAX - 1);
    if (width < 0 || height < 0 || type < 0 || difficulty < 0)
        fatal("Invalid size, tiling or difficulty");

    game_params params = { .w = width, .h = height, .type = type, .diff = difficulty };
    const char *error = validate_params(&params, true);
    if (error)
        fatal("%s", error);
    limit_resources();

    char *owned_desc = NULL, *aux = NULL;
    const char *desc = argv[6];
    if (generate) {
        size_t length = strlen(desc);
        if (!length || length > 64 || desc[strspn(desc, "0123456789abcdef")])
            fatal("Invalid seed");
        random_state *random = random_new(desc, (int)length);
        desc = owned_desc = new_game_desc(&params, random, &aux, false);
        random_free(random);
    } else if (!*desc || strlen(desc) > 64000) {
        fatal("Invalid description length");
    }
    if ((error = validate_desc(&params, desc)))
        fatal("%s", error);

    game_state *puzzle = new_game(NULL, &params, desc);
    solver_state *initial = new_solver_state(puzzle, generate ? difficulty : DIFF_HARD);
    solver_state *solution = solve_game_rec(initial);
    bool solved = solution->solver_status == SOLVER_SOLVED && check_completion(solution->state);
    if (generate && !solved)
        fatal("Loopy could not deduce a valid solution");

    grid *g = puzzle->game_grid;
    printf("{\"vertices\":[");
    for (int i = 0; i < g->num_dots; i++)
        printf("%s[%d,%d]", i ? "," : "", g->dots[i]->x, g->dots[i]->y);
    printf("],\"edges\":[");
    for (int i = 0; i < g->num_edges; i++)
        printf("%s[%d,%d]", i ? "," : "", g->edges[i]->dot1->index, g->edges[i]->dot2->index);
    printf("],\"faces\":[");
    for (int i = 0; i < g->num_faces; i++) {
        grid_face *f = g->faces[i];
        grid_find_incentre(f);
        printf("%s{\"x\":%d,\"y\":%d,\"clue\":%d,\"edges\":[",
               i ? "," : "", f->ix, f->iy, puzzle->clues[i]);
        for (int j = 0; j < f->order; j++)
            printf("%s%d", j ? "," : "", f->edges[j]->index);
        printf("]}");
    }
    if (solved) {
        printf("],\"solution\":\"");
        for (int i = 0; i < g->num_edges; i++)
            putchar(solution->state->lines[i] == LINE_YES ? '#' : '.');
        puts("\"}");
    } else {
        puts("]}");
    }
    free_solver_state(solution);
    free_solver_state(initial);
    free_game(puzzle);
    sfree(aux);
    sfree(owned_desc);
}
