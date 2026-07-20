namespace PortfolioTracker.Core.Models;

public enum TxType { Buy = 0, Sell = 1 }
public enum CashFlowType { Deposit = 0, Dividend = 1 }
public enum LotSource { Manual = 0, ChaseImport = 1, OpeningBalance = 2 }
public enum TargetDirection { AtOrAbove = 0, AtOrBelow = 1 }
public enum TaxTreatment { Taxable = 0, RothIRA = 1, TraditionalIRA = 2 }

/// How a disallowed loss relates to its replacement lot. Same-account and
/// cross-account-taxable washes defer the loss into the replacement lot's
/// basis; an IRA replacement (Rev. Rul. 2008-5) destroys the loss permanently.
public enum WashKind { SameAccount = 0, CrossAccountTaxable = 1, IraPermanent = 2 }
