/* conditions.hpp - internal: the decision-tree condition evaluator shared by
 * ce_firmware_evaluate, ce_settings_evaluate, ce_slots_evaluate and
 * ce_project_validate. Not part of the ABI. */

#pragma once

#include "../../extern/cjson/cJSON.h"

/* {"slot": id[, "extension": e]}, {"setting": n, "is"/"in"}, all/any/not;
 * anything malformed evaluates false. */
bool ceEvalCondition(const cJSON *cond, const cJSON *slots, const cJSON *settings);
