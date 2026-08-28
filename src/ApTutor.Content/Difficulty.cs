namespace ApTutor.Content;

/// A content axis orthogonal to the existing multi-set versioning (see the multi-set library plan)
/// — a node can have several independently-approved sets at each difficulty, not one or the other.
/// Serializes as a lowercase string ("easy"/"medium"/"hard") wherever ContentHash.CanonicalOptions
/// is used, matching every other enum this project puts on the wire.
public enum Difficulty { Easy, Medium, Hard }
