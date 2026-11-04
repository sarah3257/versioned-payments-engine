namespace Payments.Api.Domain;
public record PublicationPackage(PublishModel Model,List<PublishComponent> Components,List<PublishPrice> Prices);
