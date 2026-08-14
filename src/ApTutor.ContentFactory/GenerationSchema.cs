using System.Text.Json.Nodes;

namespace ApTutor.ContentFactory;

/// The JSON schema handed to Claude as a tool's input_schema. Loosely typed on the scene-op shape
/// on purpose: this schema *guides* generation, but the real validation is Generator.cs
/// deserializing straight into the actual SceneOp record types — a malformed or out-of-vocabulary
/// op fails loudly there and that node is skipped, it never silently ships wrong content.
public static class GenerationSchema
{
    public static JsonNode NodeContentSchema() => new JsonObject
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "walkthroughText", "practiceItems", "walkthroughSteps" },
        ["properties"] = new JsonObject
        {
            ["walkthroughText"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "A short (2-4 sentence) original explanation of the concept, " +
                                   "written for a high-school AP CS A student. Plain prose, no markdown.",
            },
            ["practiceItems"] = PracticeItemsArraySchema(),
            ["walkthroughSteps"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 3,
                ["description"] = "An animated walkthrough: a sequence of scene-primitive steps, " +
                                   "starting from an empty state and building up one small change " +
                                   "at a time, exactly as a teacher would narrate it live.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray { "caption", "ops" },
                    ["properties"] = new JsonObject
                    {
                        ["caption"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "One sentence narrating what this step shows — read aloud by TTS.",
                        },
                        ["sourceLine"] = new JsonObject { ["type"] = new JsonArray { "integer", "null" } },
                        ["ops"] = new JsonObject { ["type"] = "array", ["items"] = SceneOpSchema() },
                    },
                },
            },
        },
    };

    /// Dev-only "refresh questions" (see Generator.RegeneratePracticeItemsAsync): just the
    /// practiceItems shape, no walkthrough — a single node's questions regenerate in one small,
    /// cheap call instead of the full node content pack.
    public static JsonNode PracticeItemsOnlySchema() => new JsonObject
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "practiceItems" },
        ["properties"] = new JsonObject { ["practiceItems"] = PracticeItemsArraySchema() },
    };

    private static JsonNode PracticeItemsArraySchema() => new JsonObject
    {
        ["type"] = "array",
        ["minItems"] = 2,
        ["maxItems"] = 3,
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray { "prompt", "choices", "correctIndex", "explanation" },
            ["properties"] = new JsonObject
            {
                ["prompt"] = new JsonObject { ["type"] = "string" },
                ["choices"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 4,
                    ["maxItems"] = 4,
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
                ["correctIndex"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 3 },
                ["explanation"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Why the correct choice is right AND why at least one distractor is wrong.",
                },
            },
        },
    };

    private static JsonNode SceneOpSchema() => new JsonObject
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "op" },
        ["description"] =
            "One scene primitive operation. 'op' selects the shape; only include the fields that " +
            "op uses, named exactly as shown. Vocabulary — " +
            "framePush{frameId,methodSig} framePop{frameId} " +
            "memCellSet{frameId,name,type,value} memCellFlash{frameId,name} " +
            "heapAlloc{objId,className,fields:[{key,value}]} fieldSet{objId,field,value} " +
            "refSet{frameId,varName,targetObjId(nullable)} lineHighlight{line} " +
            "arrayAlloc{arrId,elementType,initialValues:[string]} arrayWrite{arrId,index,value} " +
            "grid2dAlloc{gridId,rows,cols,elementType,defaultValue} grid2dWrite{gridId,row,col,value} " +
            "callTreeNode{nodeId,parentId(nullable),label} callTreeReturn{nodeId,returnValue} " +
            "exprPush{exprId,text} exprResolve{exprId,value} boolGlow{exprId,value(boolean)}. " +
            "frameId/objId/arrId/gridId/exprId/nodeId are your own short stable string ids " +
            "(e.g. \"main\", \"1\", \"a1\") — reuse the same id across ops that refer to the same thing.",
        ["properties"] = new JsonObject
        {
            ["op"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray
                {
                    "framePush", "framePop", "memCellSet", "memCellFlash", "heapAlloc", "fieldSet",
                    "refSet", "lineHighlight", "arrayAlloc", "arrayWrite", "grid2dAlloc", "grid2dWrite",
                    "callTreeNode", "callTreeReturn", "exprPush", "exprResolve", "boolGlow",
                },
            },
        },
        ["additionalProperties"] = true,
    };
}
