import { parseBudgetFilterParams } from "./budget-filters";

const ENVELOPE = "11111111-1111-1111-1111-111111111111";
const OCCASION = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";

describe("parseBudgetFilterParams", () => {
  it("reads both filters when they are UUIDs, in any case", () => {
    expect(
      parseBudgetFilterParams(
        new URLSearchParams({ envelopeId: ENVELOPE, occasionId: OCCASION }),
      ),
    ).toEqual({ envelopeId: ENVELOPE, occasionId: OCCASION });
  });

  it("leaves absent filters undefined", () => {
    expect(parseBudgetFilterParams(new URLSearchParams())).toEqual({
      envelopeId: undefined,
      occasionId: undefined,
    });
  });

  it("drops malformed and empty values instead of sending them to the API", () => {
    expect(
      parseBudgetFilterParams(
        new URLSearchParams({
          envelopeId: "not-a-uuid",
          occasionId: "",
        }),
      ),
    ).toEqual({ envelopeId: undefined, occasionId: undefined });
    expect(
      parseBudgetFilterParams(
        new URLSearchParams({ envelopeId: `${ENVELOPE}x` }),
      ).envelopeId,
    ).toBeUndefined();
  });
});
